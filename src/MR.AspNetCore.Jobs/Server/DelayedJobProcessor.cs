using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MR.AspNetCore.Jobs.Models;
using MR.AspNetCore.Jobs.Server.States;
using MR.AspNetCore.Jobs.Util;

namespace MR.AspNetCore.Jobs.Server
{
	public class DelayedJobProcessor : IProcessor
	{
		private readonly TimeSpan _pollingDelay;
		protected JobsOptions _options;
		protected ILogger _logger;
		internal static readonly AutoResetEvent PulseEvent = new AutoResetEvent(true);
		private IStateChanger _stateChanger;

		public DelayedJobProcessor(
			JobsOptions options,
			IStateChanger stateChanger,
			ILogger<DelayedJobProcessor> logger)
		{
			_options = options;
			_stateChanger = stateChanger;
			_logger = logger;
			_pollingDelay = TimeSpan.FromSeconds(_options.PollingDelay);
		}

		public bool Waiting { get; private set; }

		public Task ProcessAsync(ProcessingContext context)
		{
			if (context == null) throw new ArgumentNullException(nameof(context));
			return ProcessCoreAsync(context);
		}

		public async Task ProcessCoreAsync(ProcessingContext context)
		{
			try
			{
				await Step(context);

				context.ThrowIfStopping();

				Waiting = true;
				var token = GetTokenToWaitOn(context);
				await WaitHandleEx.WaitAnyAsync(PulseEvent, token.WaitHandle, _pollingDelay);
			}
			finally
			{
				Waiting = false;
			}
		}

		private async Task Step(ProcessingContext context)
		{
			using (var connection = context.Storage.GetConnection())
			{
				var fetched = default(IFetchedJob);
				while (
					!context.IsStopping &&
					(fetched = await connection.FetchNextJobAsync()) != null)
				{
					using (fetched)
					using (var scopedContext = context.CreateScope())
					{
						var job = await connection.GetJobAsync(fetched.JobId);
						var invocationData = Helper.FromJson<InvocationData>(job.Data);
						var method = invocationData.Deserialize();
						var factory = scopedContext.Provider.GetService<IJobFactory>();

						var instance = default(object);
						if (!method.Method.IsStatic)
						{
							instance = factory.Create(method.Type);
						}

						try
						{
							var sp = Stopwatch.StartNew();
							var result = await ExecuteJob(method, instance);
							sp.Stop();

							IState newState = null;
							if (!result.Succeeded)
							{
								var shouldRetry = await UpdateJobForRetryAsync(instance, job, connection);
								if (shouldRetry)
								{
									_logger.LogWarning(
										$"Job failed to execute: '{result.Message}'. Will retry later.");
								}
								else
								{
									newState = new FailedState();
									_logger.LogWarning(
										$"Job failed to execute: '{result.Message}'.");
									// TODO: Send to DJQ
								}
							}
							else
							{
								newState = new SucceededState();
							}

							if (newState != null)
							{
								using (var transaction = connection.CreateTransaction())
								{
									if (newState != null)
									{
										_stateChanger.ChangeState(job, newState, transaction);
									}
									transaction.UpdateJob(job);
									await transaction.CommitAsync();
								}
							}

							fetched.RemoveFromQueue();
							_logger.LogInformation(
								"Job executed succesfully. Took: {seconds} secs.",
								sp.Elapsed.TotalSeconds);
						}
						catch (Exception ex)
						{
							var shouldRetry = await UpdateJobForRetryAsync(instance, job, connection);
							if (shouldRetry)
							{
								_logger.LogWarning(
									$"Job failed to execute: '{ex.Message}'. Requeuing for another retry.");
								fetched.Requeue();
							}
							else
							{
								_logger.LogWarning(
									$"Job failed to execute: '{ex.Message}'.");
								// TODO: Send to DJQ
							}
						}
					}
				}
			}
		}

		private async Task<ExecuteJobResult> ExecuteJob(MethodInvocation method, object instance)
		{
			try
			{
				var result = method.Method.Invoke(instance, method.Args.ToArray()) as Task;
				if (result != null)
				{
					await result;
				}
				return ExecuteJobResult.Success;
			}
			catch (Exception ex)
			{
				return new ExecuteJobResult(false, ex.Message);
			}
		}

		private async Task<bool> UpdateJobForRetryAsync(object instance, Job job, IStorageConnection connection)
		{
			var retryBehavior =
				(instance as IRetryable)?.RetryBehavior ??
				RetryBehavior.DefaultRetry;

			if (!retryBehavior.Retry)
			{
				return false;
			}

			var now = DateTime.UtcNow;
			var retries = ++job.Retries;
			if (retries >= retryBehavior.RetryCount)
			{
				return false;
			}

			var due = job.Added.AddSeconds(retryBehavior.RetryIn(retries));
			job.Due = due;
			using (var transaction = connection.CreateTransaction())
			{
				transaction.UpdateJob(job);
				await transaction.CommitAsync();
			}
			return true;
		}

		protected virtual CancellationToken GetTokenToWaitOn(ProcessingContext context)
		{
			return context.CancellationToken;
		}

		private class ExecuteJobResult
		{
			public static readonly ExecuteJobResult Success = new ExecuteJobResult(true);

			public ExecuteJobResult(bool succeeded, string message = null)
			{
				Succeeded = succeeded;
				Message = message;
			}

			public bool Succeeded { get; set; }
			public string Message { get; set; }
		}
	}
}
