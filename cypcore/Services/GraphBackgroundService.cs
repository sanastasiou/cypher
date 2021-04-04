// CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;
using System.Threading;
using System.Threading.Tasks;
using CYPCore.Consensus;
using CYPCore.Extensions;
using CYPCore.Extentions;
using CYPCore.Ledger;
using Microsoft.Extensions.Hosting;
using Serilog;
using static System.Threading.Tasks.Task;

namespace CYPCore.Services
{
    public class GraphBackgroundService : BackgroundService
    {
        private readonly IGraph _graph;
        private readonly PbftOptions _pBftOptions;
        private readonly ILogger _logger;

        private bool _applicationRunning = true;

        public GraphBackgroundService(IGraph graph, IHostApplicationLifetime applicationLifetime, ILogger logger)
        {
            _graph = graph;
            _pBftOptions = new PbftOptions();
            _logger = logger.ForContext("SourceContext", nameof(GraphBackgroundService));

            applicationLifetime.ApplicationStopping.Register(OnApplicationStopping);
        }

        /// <summary>
        /// 
        /// </summary>
        private void OnApplicationStopping()
        {
            _logger.Here().Information("Application stopping");
            _applicationRunning = false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Yield();

                while (_applicationRunning)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    if (!_applicationRunning) continue;

                    var interval = _pBftOptions.RoundInterval;
                    var start = DateTime.Now.Truncate(interval);
                    var workDuration = _pBftOptions.InitialWorkDuration;
                    var next = start.Add(new TimeSpan(interval));
                    var workStart = next.Add(new TimeSpan(-workDuration));
                    var timeSpan = workStart.Subtract(DateTime.Now);

                    await Delay((int)Math.Abs(timeSpan.TotalMilliseconds), stoppingToken);

                    try
                    {
                        await _graph.Ready();
                        await _graph.WriteAsync(100, stoppingToken);
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Graph process error");
            }
        }
    }
}