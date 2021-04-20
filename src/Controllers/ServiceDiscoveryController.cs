﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Articulate.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Steeltoe.Common.Discovery;
using Steeltoe.Discovery;

namespace Articulate.Controllers
{
    public class ServiceDiscoveryController : Controller
    {
        private readonly ILogger<ServiceDiscoveryController> _log;
        private readonly AppEnv _app;
        private readonly IDiscoveryClient _discoveryClient;

        public ServiceDiscoveryController(ILogger<ServiceDiscoveryController> log, AppEnv app, IDiscoveryClient discoveryClient)
        {
            _log = log;
            _app = app;
            _discoveryClient = discoveryClient;
        }

        public IActionResult Index() => View();
        
        

        public IEnumerable<DiscoveredService> GetServiceDiscoveryInstances(bool includeSelf = false)
        {
            return _discoveryClient.Services
                .Select(serviceName => new DiscoveredService
                {
                    Name = serviceName, 
                    Urls = _discoveryClient.GetInstances(serviceName)
                        .Where(service => includeSelf || service != _discoveryClient.GetLocalServiceInstance())
                        .Select(x => x.Uri.ToString())
                        .ToList()
                })
                .ToList(); 
        }

        
        public async Task<string> Ping([FromServices]IDiscoveryClient discoveryClient, string targets)
        {
            var pong = string.Empty;
            if (!string.IsNullOrEmpty(targets))
            {
                var httpClient = new HttpClient(new DiscoveryHttpClientHandler(discoveryClient));
                _log.LogTrace($"Ping received. Remaining targets: {targets}");
                var allTargets = targets.Split(",").Where(x => x != _app.AppName).ToList();
                
                if (allTargets.Any())
                {
                    var nextTarget = allTargets.First();
                    var remainingTargets = string.Join(",", allTargets.Skip(1));
                    try
                    {
                        _log.LogInformation($"Sending ping request to {nextTarget}");
                        pong = await httpClient.GetStringAsync($"https://{nextTarget}/ping/?targets={remainingTargets}");
                    }
                    catch (Exception e)
                    {
                        _log.LogError(e, $"Call to {nextTarget} failed");
                        pong = $"{nextTarget} failed to answer";
                    }
                }

            }
            return pong.Insert(0, $"Pong from {_app.AppName}\n");
        }
    }
}