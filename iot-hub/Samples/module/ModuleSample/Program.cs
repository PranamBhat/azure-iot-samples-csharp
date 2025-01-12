﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CommandLine;
using Microsoft.Azure.Devices.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Devices.Client.Samples
{
    public class Program
    {
        private const string SdkEventProviderPrefix = "Microsoft-Azure-";
        
        /// <summary>
        /// A sample for illustrating how a module client handles its connection status updates,
        /// while sending telemetry to IoT Hub, and sending and receiving twin property updates.
        /// </summary>
        /// <param name="args">
        /// Run with `--help` to see a list of required and optional parameters.
        /// </param>
        public static async Task Main(string[] args)
        {
            // Parse application parameters
            Parameters parameters = null;
            ParserResult<Parameters> result = Parser.Default.ParseArguments<Parameters>(args)
                .WithParsed(parsedParams =>
                {
                    parameters = parsedParams;
                })
                .WithNotParsed(errors =>
                {
                    Environment.Exit(1);
                });

            // Set up logging
            ILoggerFactory loggerFactory = new LoggerFactory();
            loggerFactory.AddColorConsoleLogger(
                new ColorConsoleLoggerConfiguration
                {
                    // The SDK logs are written at Trace level. Set this to LogLevel.Trace to get ALL logs.
                    MinLogLevel = LogLevel.Debug,
                });
            var logger = loggerFactory.CreateLogger<Program>();

            // Instantiating this seems to do all we need for outputting SDK events to our console log.
            _ = new ConsoleEventListener(SdkEventProviderPrefix, logger);

            // Run the sample
            var runningTime = parameters.ApplicationRunningTime != null
                ? TimeSpan.FromSeconds((double)parameters.ApplicationRunningTime)
                : Timeout.InfiniteTimeSpan;

            var sample = new ModuleSample(parameters.GetConnectionStrings(), parameters.TransportType, logger);
            await sample.RunSampleAsync(runningTime);

            logger.LogInformation("Done.");
        }
    }
}
