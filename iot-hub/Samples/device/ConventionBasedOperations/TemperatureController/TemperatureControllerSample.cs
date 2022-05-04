﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Microsoft.Azure.Devices.Client.Samples
{
    public class TemperatureControllerSample
    {
        // The default reported "value", "ad" and "av" for each "Thermostat" component on the client initial startup.
        // See https://docs.microsoft.com/en-us/azure/iot-develop/concepts-convention#writable-properties for more details in acknowledgment responses.
        private const double DefaultPropertyValue = 0d;
        private const string DefaultACKDescription = "Initialized with default value";
        private const long DefaultACKVersion = 0L;

        private const string TargetTemperatureProperty = "targetTemperature";
        
        private const string Thermostat1 = "thermostat1";
        private const string Thermostat2 = "thermostat2";

        private static readonly Random s_random = new Random();
        private static readonly TimeSpan s_sleepDuration = TimeSpan.FromSeconds(5);

        private readonly DeviceClient _deviceClient;
        private readonly ILogger _logger;

        // Dictionary to hold the temperature updates sent over each "Thermostat" component.
        // NOTE: Memory constrained devices should leverage storage capabilities of an external service to store this
        // information and perform computation.
        // See https://docs.microsoft.com/en-us/azure/event-grid/compare-messaging-services for more details.
        private readonly Dictionary<string, Dictionary<DateTimeOffset, double>> _temperatureReadingsDateTimeOffset =
            new Dictionary<string, Dictionary<DateTimeOffset, double>>();

        // Dictionary to hold the current temperature for each "Thermostat" component.
        private readonly Dictionary<string, double> _temperature = new Dictionary<string, double>();

        // Dictionary to hold the max temperature since last reboot, for each "Thermostat" component.
        private readonly Dictionary<string, double> _maxTemp = new Dictionary<string, double>();

        // A safe initial value for caching the writable properties version is 1, so the client
        // will process all previous property change requests and initialize the device application
        // after which this version will be updated to that, so we have a high water mark of which version number
        // has been processed.
        private static long s_localWritablePropertiesVersion = 1;

        public TemperatureControllerSample(DeviceClient deviceClient, ILogger logger)
        {
            _deviceClient = deviceClient ?? throw new ArgumentNullException($"{nameof(deviceClient)} cannot be null.");
            _logger = logger ?? LoggerFactory.Create(builer => builer.AddConsole()).CreateLogger<TemperatureControllerSample>();
        }

        public async Task PerformOperationsAsync(CancellationToken cancellationToken)
        {
            // Set handler to receive and respond to connection status changes.
            _deviceClient.SetConnectionStatusChangesHandler(async (status, reason) =>
            {
                _logger.LogDebug($"Connection status change registered - status={status}, reason={reason}.");

                // Call GetWritablePropertiesAndHandleChangesAsync() to get writable properties from the server once the connection status changes into Connected.
                // This can get back "lost" property updates in a device reconnection from status Disconnected_Retrying or Disconnected.
                if (status == ConnectionStatus.Connected)
                {
                    await GetWritablePropertiesAndHandleChangesAsync();
                }
            });

            // Set handler to receive and respond to writable property update requests.
            _logger.LogDebug("Subscribe to writable property updates.");
            await _deviceClient.SubscribeToWritablePropertyUpdateRequestsAsync(HandlePropertyUpdatesAsync, cancellationToken);

            // Set handler to receive and respond to commands.
            _logger.LogDebug($"Subscribe to commands.");
            await _deviceClient.SubscribeToCommandsAsync(HandleCommandsAsync, cancellationToken);

            // For each component, check if the corresponding properties (both writable and reported) are empty on the initial startup.
            // If so, report the default values with ACK to the hub.
            await CheckEmptyProperties(Thermostat1, cancellationToken);
            await CheckEmptyProperties(Thermostat2, cancellationToken);

            // Report device information on "deviceInformation" component.
            // This is a component-level property update call.
            await UpdateDeviceInformationPropertyAsync(cancellationToken);

            // Verify if the device has previously reported the current value for property "serialNumber".
            // If the expected value has not been previously reported then send device serial number over property update.
            // This is a top-level property update call.
            await SendDeviceSerialNumberPropertyIfNotCurrentAsync(cancellationToken);

            bool temperatureReset = true;
            _maxTemp[Thermostat1] = 0d;
            _maxTemp[Thermostat2] = 0d;

            // Periodically send "temperature" over telemetry - on "Thermostat" components.
            // Send "maxTempSinceLastReboot" over property update, when a new max temperature is reached - on "Thermostat" components.
            while (!cancellationToken.IsCancellationRequested)
            {
                if (temperatureReset)
                {
                    // Generate a random value between 5.0°C and 45.0°C for the current temperature reading for each "Thermostat" component.
                    _temperature[Thermostat1] = GenerateTemperatureWithinRange(45, 5);
                    _temperature[Thermostat2] = GenerateTemperatureWithinRange(45, 5);
                }

                // Send temperature updates over telemetry and the value of max temperature since last reboot over property update.
                // Both of these are component-level calls.
                await SendTemperatureAsync(Thermostat1, cancellationToken);
                await SendTemperatureAsync(Thermostat2, cancellationToken);

                // Send working set of device memory over telemetry.
                // This is a top-level telemetry call.
                await SendDeviceMemoryTelemetryAsync(cancellationToken);

                temperatureReset = _temperature[Thermostat1] == 0 && _temperature[Thermostat2] == 0;
                await Task.Delay(s_sleepDuration);
            }
        }

        private async Task GetWritablePropertiesAndHandleChangesAsync()
        {
            ClientProperties properties = await _deviceClient.GetClientPropertiesAsync();
            ClientPropertyCollection writableProperties = properties.WritablePropertyRequests;
            long serverWritablePropertiesVersion = writableProperties.Version;

            // Check if the writable property version is outdated on the local side.
            // For the purpose of this sample, we'll only check the writable property versions between local and server
            // side without comparing the property values.
            if (serverWritablePropertiesVersion > s_localWritablePropertiesVersion)
            {
                _logger.LogDebug($"The writable property version cached on local is changing from {s_localWritablePropertiesVersion} to {serverWritablePropertiesVersion}.");

                foreach (KeyValuePair<string, object> writableProperty in writableProperties)
                {
                    switch (writableProperty.Key)
                    {
                        case Thermostat1:
                        case Thermostat2:
                            string componentName = writableProperty.Key;

                            // The SubscribeToWritablePropertyUpdateRequestsAsync callback makes the writable property requests available as a
                            // WritableClientProperty, which provides convenience methods for acknowledging the requests. Since property
                            // update requests that were potentially lost during reconnection can be retrieved only as a collection of property
                            // values, and not as a collection of WritableClientProperty, we will need to create the property response ack by ourselves.
                            if (writableProperties.TryGetValue(componentName, TargetTemperatureProperty, out double targetTemperatureValue))
                            { 
                                _logger.LogDebug($"Property: Received - component=\"{componentName}\", [ \"{TargetTemperatureProperty}\": {targetTemperatureValue}°C ].");

                                _temperature[componentName] = targetTemperatureValue;

                                // The serailizer used by the client can be used to format the writable property update response according
                                // to the format specified by IoT plug and play conventions.
                                var propertyValue = _deviceClient.PayloadConvention.PayloadSerializer.CreateWritablePropertyResponse(
                                    _temperature[componentName], 
                                    ClientResponseCodes.OK, 
                                    serverWritablePropertiesVersion);

                                var reportedProperty = new ClientPropertyCollection();
                                reportedProperty.AddComponentProperty(componentName, TargetTemperatureProperty, propertyValue);

                                ClientPropertiesUpdateResponse updateResponse = await _deviceClient.UpdateClientPropertiesAsync(reportedProperty);

                                _logger.LogDebug($"Property: Update - component=\"{componentName}\", {reportedProperty.GetSerializedString()} is {nameof(ClientResponseCodes.OK)} " +
                                    $"with a version of {updateResponse.Version}.");

                                break;
                            }
                            else
                            {
                                _logger.LogWarning($"Property: Received an unrecognized property update from service for component {componentName}:" +
                                    $"\n[ {writableProperty.Value} ].");
                                break;
                            }

                        default:
                            _logger.LogWarning($"Property: Received an unrecognized property update from service:" +
                                $"\n[ {writableProperty.Key}: {writableProperty.Value} ].");
                            break;
                    }
                }

                s_localWritablePropertiesVersion = writableProperties.Version;
                _logger.LogDebug($"The writable property version on local is currently {s_localWritablePropertiesVersion}.");
            }
        }

        // The callback to handle property update requests.
        private async Task HandlePropertyUpdatesAsync(ClientPropertyCollection writableProperties)
        {
            foreach (KeyValuePair<string, object> writableProperty in writableProperties)
            {
                // The dispatcher key will be either a top-level property name or a component name.
                switch (writableProperty.Key)
                {
                    case Thermostat1:
                    case Thermostat2:
                        string componentName = writableProperty.Key;
                        if (writableProperties.TryGetValue(componentName, TargetTemperatureProperty, out WritableClientProperty targetTemperatureRequested))
                        {
                            await HandleTargetTemperatureUpdateRequestAsync(componentName, targetTemperatureRequested);
                            break;
                        }
                        else
                        {
                            _logger.LogWarning($"Property: Received an unrecognized property update from service for component {componentName}:" +
                                $"\n[ {writableProperty.Value} ].");
                            break;
                        }

                    default:
                        _logger.LogWarning($"Property: Received an unrecognized property update from service:" +
                            $"\n[ {writableProperty.Key}: {writableProperty.Value} ].");
                        break;
                }
            }

            s_localWritablePropertiesVersion = writableProperties.Version;
            _logger.LogDebug($"The writable property version on local is currently {s_localWritablePropertiesVersion}.");
        }

        // The callback to handle target temperature property update requests for a component.
        private async Task HandleTargetTemperatureUpdateRequestAsync(string componentName, WritableClientProperty targetTemperatureUpdateRequest)
        {
            double targetTemperatureValue = SystemTextJsonPayloadSerializer.Instance.ConvertFromObject<double>(targetTemperatureUpdateRequest.Value);
            _logger.LogDebug($"Property: Received - component=\"{componentName}\", [ \"{TargetTemperatureProperty}\": {targetTemperatureValue}°C ].");

            _temperature[componentName] = targetTemperatureValue;

            var reportedProperty = new ClientPropertyCollection();
            reportedProperty.AddComponentProperty(componentName, TargetTemperatureProperty, targetTemperatureUpdateRequest.AcknowledgeWith(ClientResponseCodes.OK));

            ClientPropertiesUpdateResponse updateResponse = await _deviceClient.UpdateClientPropertiesAsync(reportedProperty);

            _logger.LogDebug($"Property: Update - component=\"{componentName}\", {reportedProperty.GetSerializedString()} is {nameof(ClientResponseCodes.OK)} " +
                $"with a version of {updateResponse.Version}.");
        }

        // The callback to handle command invocation requests.
        private Task<CommandResponse> HandleCommandsAsync(CommandRequest commandRequest)
        {
            // In this approach, we'll first switch through the component name returned and handle each component-level command.
            // For the "default" case, we'll first check if the component name is null.
            // If null, then this would be a top-level command request, so we'll switch through each top-level command.
            // If not null, then this is a component-level command that has not been implemented.

            // Switch through CommandRequest.ComponentName to handle all component-level commands.
            switch (commandRequest.ComponentName)
            {
                case Thermostat1:
                case Thermostat2:
                    // For each component, switch through CommandRequest.CommandName to handle the specific component-level command.
                    switch (commandRequest.CommandName)
                    {
                        case "getMaxMinReport":
                            return HandleMaxMinReportCommandAsync(commandRequest);

                        default:
                            _logger.LogWarning($"Received a command request that isn't" +
                                $" implemented - component name = {commandRequest.ComponentName}, command name = {commandRequest.CommandName}");

                            return Task.FromResult(new CommandResponse(ClientResponseCodes.NotFound));
                    }

                // For the default case, first check if CommandRequest.ComponentName is null.
                default:
                    // If CommandRequest.ComponentName is null, then this is a top-level command request.
                    if (commandRequest.ComponentName == null)
                    {
                        // Switch through CommandRequest.CommandName to handle all top-level commands.
                        switch (commandRequest.CommandName)
                        {
                            case "reboot":
                                return HandleRebootCommandAsync(commandRequest);

                            default:
                                _logger.LogWarning($"Received a command request that isn't" +
                                    $" implemented - command name = {commandRequest.CommandName}");

                                return Task.FromResult(new CommandResponse(ClientResponseCodes.NotFound));
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Received a command request that isn't" +
                            $" implemented - component name = {commandRequest.ComponentName}, command name = {commandRequest.CommandName}");

                        return Task.FromResult(new CommandResponse(ClientResponseCodes.NotFound));
                    }
            }
        }

        // The callback to handle top-level "reboot" command.
        // This method will send a temperature update (of 0°C) over telemetry for both associated components.
        private async Task<CommandResponse> HandleRebootCommandAsync(CommandRequest commandRequest)
        {
            try
            {
                int delay = commandRequest.GetPayload<int>();

                _logger.LogDebug($"Command: Received - Rebooting thermostat (resetting temperature reading to 0°C after {delay} seconds).");
                await Task.Delay(delay * 1000);

                _temperature[Thermostat1] = _maxTemp[Thermostat1] = 0;
                _temperature[Thermostat2] = _maxTemp[Thermostat2] = 0;

                _temperatureReadingsDateTimeOffset.Clear();
                _logger.LogDebug($"Command: Reboot completed.");

                return new CommandResponse(ClientResponseCodes.OK);
            }
            catch (JsonReaderException ex)
            {
                _logger.LogDebug($"Command input for {commandRequest.CommandName} is invalid: {ex.Message}.");
                return new CommandResponse(ClientResponseCodes.BadRequest);
            }
        }

        // The callback to handle component-level "getMaxMinReport" command.
        // This method will returns the max, min and average temperature from the specified time to the current time.
        private Task<CommandResponse> HandleMaxMinReportCommandAsync(CommandRequest commandRequest)
        {
            try
            {
                DateTimeOffset sinceInUtc = commandRequest.GetPayload<DateTimeOffset>();
                _logger.LogDebug($"Command: Received - Generating max, min and avg temperature report since " +
                    $"{sinceInUtc.LocalDateTime}.");

                if (_temperatureReadingsDateTimeOffset.ContainsKey(commandRequest.ComponentName))
                {
                    Dictionary<DateTimeOffset, double> allReadings = _temperatureReadingsDateTimeOffset[commandRequest.ComponentName];
                    Dictionary<DateTimeOffset, double> filteredReadings = allReadings.Where(i => i.Key > sinceInUtc)
                        .ToDictionary(i => i.Key, i => i.Value);

                    if (filteredReadings != null && filteredReadings.Any())
                    {
                        var report = new TemperatureReport
                        {
                            MaximumTemperature = filteredReadings.Values.Max<double>(),
                            MinimumTemperature = filteredReadings.Values.Min<double>(),
                            AverageTemperature = filteredReadings.Values.Average(),
                            StartTime = filteredReadings.Keys.Min(),
                            EndTime = filteredReadings.Keys.Max(),
                        };

                        _logger.LogDebug($"Command: component=\"{commandRequest.ComponentName}\", MaxMinReport since {sinceInUtc.LocalDateTime}:" +
                            $" maxTemp={report.MaximumTemperature}, minTemp={report.MinimumTemperature}, avgTemp={report.AverageTemperature}, " +
                            $"startTime={report.StartTime.LocalDateTime}, endTime={report.EndTime.LocalDateTime}");

                        return Task.FromResult(new CommandResponse(report, ClientResponseCodes.OK));
                    }

                    _logger.LogDebug($"Command: component=\"{commandRequest.ComponentName}\"," +
                        $" no relevant readings found since {sinceInUtc.LocalDateTime}, cannot generate any report.");

                    return Task.FromResult(new CommandResponse(ClientResponseCodes.NotFound));
                }

                _logger.LogDebug($"Command: component=\"{commandRequest.ComponentName}\", no temperature readings sent yet," +
                    $" cannot generate any report.");

                return Task.FromResult(new CommandResponse(ClientResponseCodes.NotFound));
            }
            catch (JsonReaderException ex)
            {
                _logger.LogError($"Command input for {commandRequest.CommandName} is invalid: {ex.Message}.");

                return Task.FromResult(new CommandResponse(ClientResponseCodes.BadRequest));
            }
        }

        // Report the property values on "deviceInformation" component.
        // This is a component-level property update call.
        private async Task UpdateDeviceInformationPropertyAsync(CancellationToken cancellationToken)
        {
            const string componentName = "deviceInformation";
            var deviceInformationProperties = new Dictionary<string, object>
            {
                { "manufacturer", "element15" },
                { "model", "ModelIDxcdvmk" },
                { "swVersion", "1.0.0" },
                { "osName", "Windows 10" },
                { "processorArchitecture", "64-bit" },
                { "processorManufacturer", "Intel" },
                { "totalStorage", 256 },
                { "totalMemory", 1024 },
            };
            var deviceInformation = new ClientPropertyCollection();
            deviceInformation.AddComponentProperties(componentName, deviceInformationProperties);

            ClientPropertiesUpdateResponse updateResponse = await _deviceClient.UpdateClientPropertiesAsync(deviceInformation, cancellationToken);

            _logger.LogDebug($"Property: Update - component = '{componentName}', properties update is complete " +
                $"with a version of {updateResponse.Version}.");
        }

        // Send working set of device memory over telemetry.
        // This is a top-level telemetry call.
        private async Task SendDeviceMemoryTelemetryAsync(CancellationToken cancellationToken)
        {
            const string workingSetName = "workingSet";
            long workingSet = Process.GetCurrentProcess().PrivateMemorySize64 / 1024;
            using var telemetryMessage = new TelemetryMessage
            {
                Telemetry = { [workingSetName] = workingSet },
            };

            await _deviceClient.SendTelemetryAsync(telemetryMessage, cancellationToken);

            _logger.LogDebug($"Telemetry: Sent - {telemetryMessage.Telemetry.GetSerializedString()} in KB.");
        }

        // Verify if the device has previously reported the current value for property "serialNumber".
        // If the expected value has not been previously reported then send device serial number over property update.
        // This is a top-level property update call.
        private async Task SendDeviceSerialNumberPropertyIfNotCurrentAsync(CancellationToken cancellationToken)
        {
            const string serialNumber = "serialNumber";
            const string currentSerialNumber = "SR-123456";

            // Verify if the device has previously reported the current value for property "serialNumber".
            // If the expected value has not been previously reported then report it.

            // Retrieve the device's properties.
            ClientProperties properties = await _deviceClient.GetClientPropertiesAsync(cancellationToken);

            if (!properties.ReportedFromClient.TryGetValue(serialNumber, out string serialNumberReported)
                || serialNumberReported != currentSerialNumber)
            {
                var reportedProperties = new ClientPropertyCollection();
                reportedProperties.AddRootProperty(serialNumber, currentSerialNumber);

                ClientPropertiesUpdateResponse updateResponse = await _deviceClient.UpdateClientPropertiesAsync(reportedProperties, cancellationToken);

                _logger.LogDebug($"Property: Update - {reportedProperties.GetSerializedString()} is complete " +
                    $"with a version of {updateResponse.Version}.");
            }
        }

        // Send temperature updates over telemetry.
        // This also sends the value of max temperature since last reboot over property update.
        private async Task SendTemperatureAsync(string componentName, CancellationToken cancellationToken)
        {
            await SendTemperatureTelemetryAsync(componentName, cancellationToken);

            double maxTemp = _temperatureReadingsDateTimeOffset[componentName].Values.Max<double>();
            if (maxTemp > _maxTemp[componentName])
            {
                _maxTemp[componentName] = maxTemp;
                await UpdateMaxTemperatureSinceLastRebootAsync(componentName, cancellationToken);
            }
        }

        // Send temperature update over telemetry.
        // This is a component-level telemetry call.
        private async Task SendTemperatureTelemetryAsync(string componentName, CancellationToken cancellationToken)
        {
            const string telemetryName = "temperature";
            double currentTemperature = _temperature[componentName];

            using var telemtryMessage = new TelemetryMessage(componentName)
            {
                Telemetry = { [telemetryName] = currentTemperature },
            };

            await _deviceClient.SendTelemetryAsync(telemtryMessage, cancellationToken);

            _logger.LogDebug($"Telemetry: Sent - component=\"{componentName}\", {telemtryMessage.Telemetry.GetSerializedString()} in °C.");

            if (_temperatureReadingsDateTimeOffset.ContainsKey(componentName))
            {
                _temperatureReadingsDateTimeOffset[componentName].TryAdd(DateTimeOffset.UtcNow, currentTemperature);
            }
            else
            {
                _temperatureReadingsDateTimeOffset.TryAdd(
                    componentName,
                    new Dictionary<DateTimeOffset, double>
                    {
                        { DateTimeOffset.UtcNow, currentTemperature },
                    });
            }
        }

        // Send temperature over reported property update.
        // This is a component-level property update.
        private async Task UpdateMaxTemperatureSinceLastRebootAsync(string componentName, CancellationToken cancellationToken)
        {
            const string propertyName = "maxTempSinceLastReboot";
            double maxTemp = _maxTemp[componentName];
            var reportedProperties = new ClientPropertyCollection();
            reportedProperties.AddComponentProperty(componentName, propertyName, maxTemp);

            ClientPropertiesUpdateResponse updateResponse = await _deviceClient.UpdateClientPropertiesAsync(reportedProperties, cancellationToken);

            _logger.LogDebug($"Property: Update - component=\"{componentName}\", {reportedProperties.GetSerializedString()}" +
                $" in °C is complete with a version of {updateResponse.Version}.");
        }

        private static double GenerateTemperatureWithinRange(int max = 50, int min = 0)
        {
            return Math.Round(s_random.NextDouble() * (max - min) + min, 1);
        }

        private async Task CheckEmptyProperties(string componentName, CancellationToken cancellationToken)
        {
            ClientProperties properties = await _deviceClient.GetClientPropertiesAsync();

            ClientPropertyCollection writableProperty = properties.WritablePropertyRequests;
            ClientPropertyCollection reportedProperty = properties.ReportedFromClient;

            // Check if the device properties for the current component are empty.
            if (!writableProperty.Contains(componentName) && !reportedProperty.Contains(componentName))
            {
                await ReportInitialProperty(componentName, TargetTemperatureProperty, cancellationToken);
            }
        }

        private async Task ReportInitialProperty(string componentName, string propertyName, CancellationToken cancellationToken)
        {
            var reportedProperties = new ClientPropertyCollection();

            // Report the default value with ACK(ac=203, av=0) as part of the PnP convention.
            // "DefaultPropertyValue" is set from the device when the desired property is not set via the hub.
            var propertyValue = _deviceClient.PayloadConvention.PayloadSerializer.CreateWritablePropertyResponse(
                DefaultPropertyValue, 
                ClientResponseCodes.DeviceInitialProperty, 
                DefaultACKVersion, 
                DefaultACKDescription);

            reportedProperties.AddComponentProperty(componentName, propertyName, propertyValue);

            ClientPropertiesUpdateResponse updateResponse = await _deviceClient.UpdateClientPropertiesAsync(reportedProperties, cancellationToken);

            _logger.LogDebug($"Report the default value with ACK for \"{componentName}\" on the client initial startup.\nProperty: Update - component=\"{componentName}\", " +
                $"{reportedProperties.GetSerializedString()} is complete with a version of {updateResponse.Version}.");
        }
    }
}