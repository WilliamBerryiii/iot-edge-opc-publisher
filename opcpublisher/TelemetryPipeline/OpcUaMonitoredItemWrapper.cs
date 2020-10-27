﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Opc.Ua;
using Opc.Ua.Client;
using OpcPublisher.Configurations;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpcPublisher
{
    /// <summary>
    /// Wrapper for the OPC UA monitored item, which monitored a nodes we need to publish.
    /// </summary>
    public class OpcUaMonitoredItemWrapper
    {
        /// <summary>
        /// The notification that the data for a monitored item has changed on an OPC UA server.
        /// </summary>
        public static void MonitoredItemNotificationEventHandler(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            try
            {
                if (e == null || e.NotificationValue == null || monitoredItem == null || monitoredItem.Subscription == null || monitoredItem.Subscription.Session == null)
                {
                    return;
                }

                if (!(e.NotificationValue is MonitoredItemNotification notification))
                {
                    return;
                }

                if (!(notification.Value is DataValue value))
                {
                    return;
                }

                // filter out configured suppression status codes
                if (SettingsConfiguration.SuppressedOpcStatusCodes != null && SettingsConfiguration.SuppressedOpcStatusCodes.Contains(notification.Value.StatusCode.Code))
                {
                    Program.Instance.Logger.Debug($"Filtered notification with status code '{notification.Value.StatusCode.Code}'");
                    return;
                }

                // stop the heartbeat timer
                //HeartbeatSendTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                MessageDataModel messageData = new MessageDataModel();
                messageData.EndpointUrl = monitoredItem.Subscription.Session.ConfiguredEndpoint.EndpointUrl.ToString();
                messageData.NodeId = monitoredItem.ResolvedNodeId.ToString();
                messageData.ApplicationUri = monitoredItem.Subscription.Session.Endpoint.Server.ApplicationUri + (string.IsNullOrEmpty(SettingsConfiguration.PublisherSite) ? "" : $":{SettingsConfiguration.PublisherSite}");
                
                if (monitoredItem.DisplayName != null)
                {
                    // use the DisplayName as reported in the MonitoredItem
                    messageData.DisplayName = monitoredItem.DisplayName;
                }
                
                // use the SourceTimestamp as reported in the notification event argument in ISO8601 format
                messageData.SourceTimestamp = value.SourceTimestamp.ToString("o", CultureInfo.InvariantCulture);
                
                // use the StatusCode as reported in the notification event argument
                messageData.StatusCode = value.StatusCode.Code;
                
                // use the StatusCode as reported in the notification event argument to lookup the symbolic name
                messageData.Status = StatusCode.LookupSymbolicId(value.StatusCode.Code);
                
                if (value.Value != null)
                {
                    // use the Value as reported in the notification event argument encoded with the OPC UA JSON endcoder
                    JsonEncoder encoder = new JsonEncoder(monitoredItem.Subscription.Session.MessageContext, false);
                    value.ServerTimestamp = DateTime.MinValue;
                    value.SourceTimestamp = DateTime.MinValue;
                    value.StatusCode = StatusCodes.Good;
                    encoder.WriteDataValue("Value", value);
                    string valueString = encoder.CloseAndReturnText();
                    // we only want the value string, search for everything till the real value starts
                    // and get it
                    string marker = "{\"Value\":{\"Value\":";
                    int markerStart = valueString.IndexOf(marker, StringComparison.InvariantCulture);
                    messageData.PreserveValueQuotes = true;
                    if (markerStart >= 0)
                    {
                        // we either have a value in quotes or just a value
                        int valueLength;
                        int valueStart = marker.Length;
                        if (valueString.IndexOf("\"", valueStart, StringComparison.InvariantCulture) >= 0)
                        {
                            // value is in quotes and two closing curly brackets at the end
                            valueStart++;
                            valueLength = valueString.Length - valueStart - 3;
                        }
                        else
                        {
                            // value is without quotes with two curly brackets at the end
                            valueLength = valueString.Length - marker.Length - 2;
                            messageData.PreserveValueQuotes = false;
                        }
                        messageData.Value = valueString.Substring(valueStart, valueLength);
                    }
                }

                Program.Instance.Logger.Debug($"   ApplicationUri: {messageData.ApplicationUri}");
                Program.Instance.Logger.Debug($"   EndpointUrl: {messageData.EndpointUrl}");
                Program.Instance.Logger.Debug($"   DisplayName: {messageData.DisplayName}");
                Program.Instance.Logger.Debug($"   Value: {messageData.Value}");
                
                if (monitoredItem.Subscription == null)
                {
                    Program.Instance.Logger.Debug($"Subscription already removed. No more details available.");
                }
                else
                {
                    Program.Instance.Logger.Debug($"Enqueue a new message from subscription {(monitoredItem.Subscription == null ? "removed" : monitoredItem.Subscription.Id.ToString(CultureInfo.InvariantCulture))}");
                    Program.Instance.Logger.Debug($" with publishing interval: {monitoredItem?.Subscription?.PublishingInterval} and sampling interval: {monitoredItem?.SamplingInterval}):");
                }

                // skip event if needed
                if (_skipFirst.ContainsKey(messageData.NodeId) && _skipFirst[messageData.NodeId])
                {
                    Program.Instance.Logger.Debug($"Skipping first telemetry event for node '{messageData.DisplayName}'.");
                    _skipFirst[messageData.NodeId] = false;
                }
                else
                {
                    // enqueue the telemetry event
                    HubClientWrapper.Enqueue(messageData);
                }
            }
            catch (Exception ex)
            {
                Program.Instance.Logger.Error(ex, "Error processing monitored item notification");
            }
        }

        public static Dictionary<string, bool> _skipFirst = new Dictionary<string, bool>();
    }
}
