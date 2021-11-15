//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.XEvent;
using Microsoft.SqlServer.XEvent.XELite;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlTools.ServiceLayer.Profiler.Contracts;
using Microsoft.SqlTools.Utility;

namespace Microsoft.SqlTools.ServiceLayer.Profiler
{
    /// <summary>
    /// Class to monitor active profiler sessions
    /// </summary>
    public class ProfilerSessionMonitor : IProfilerSessionMonitor
    {
        private object sessionsLock = new object();

        private object listenersLock = new object();

        private Task processorThread = null;

        private struct Viewer
        {
            public string Id { get; set; }
            public bool active { get; set; }

            public int xeSessionId { get; set; }

            public Viewer(string Id, bool active, int xeId)
            {
                this.Id = Id;
                this.active = active;
                this.xeSessionId = xeId;
            }
        };

        // XEvent Session Id's matched to the Profiler Id's watching them
        private Dictionary<int, List<string>> sessionViewers = new Dictionary<int, List<string>>();

        // XEvent Session Id's matched to their Profiler Sessions
        private Dictionary<int, ProfilerSession> monitoredSessions = new Dictionary<int, ProfilerSession>();

        // XEvent Session Id's matched to their Profiler Sessions
        private Dictionary<int, CancellationTokenSource> monitoredCancellationTokenSources = new Dictionary<int, CancellationTokenSource>();

        // ViewerId -> Viewer objects
        private Dictionary<string, Viewer> allViewers = new Dictionary<string, Viewer>();

        private List<IProfilerSessionListener> listeners = new List<IProfilerSessionListener>();

        /// <summary>
        /// Registers a session event Listener to receive a callback when events arrive
        /// </summary>
        public void AddSessionListener(IProfilerSessionListener listener)
        {
            lock (this.listenersLock)
            {
                this.listeners.Add(listener);
            }
        }

        public bool StartMonitoringSession(string viewerId, IXEventSession session, ConnectionInfo connInfo)
        {
            lock (this.sessionsLock)
            {
                // start the monitoring thread
                if (this.processorThread == null)
                {
                    this.processorThread = Task.Factory.StartNew(ProcessStreams);
                }

                // create new profiling session if needed
                if (!this.monitoredSessions.ContainsKey(session.Id))
                {
                    var profilerSession = new ProfilerSession();
                    profilerSession.ConnectionInfo = connInfo;
                    profilerSession.XEventSession = session;

                    this.monitoredSessions.Add(session.Id, profilerSession);
                }

                // create a new viewer, or configure existing viewer
                Viewer viewer;
                if (!this.allViewers.TryGetValue(viewerId, out viewer))
                {
                    viewer = new Viewer(viewerId, true, session.Id);
                    allViewers.Add(viewerId, viewer);
                }
                else
                {
                    viewer.active = true;
                    viewer.xeSessionId = session.Id;
                }

                // add viewer to XEvent session viewers
                List<string> viewers;
                if (this.sessionViewers.TryGetValue(session.Id, out viewers))
                {
                    viewers.Add(viewerId);
                }
                else
                {
                    viewers = new List<string> { viewerId };
                    sessionViewers.Add(session.Id, viewers);
                }
            }

            return true;
        }

        /// <summary>
        /// Stop monitoring the session watched by viewerId
        /// </summary>
        public bool StopMonitoringSession(string viewerId, out ProfilerSession session)
        {
            lock (this.sessionsLock)
            {
                Viewer v;
                if (this.allViewers.TryGetValue(viewerId, out v))
                {
                    return RemoveSession(v.xeSessionId, out session);
                }
                else
                {
                    session = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// Toggle the pause state for the viewer
        /// </summary>
        public void PauseViewer(string viewerId)
        {
            lock (this.sessionsLock)
            {
                Viewer v = this.allViewers[viewerId];
                v.active = !v.active;
                this.allViewers[viewerId] = v;
            }
        }

        private bool RemoveSession(int sessionId, out ProfilerSession session)
        {
            lock (this.sessionsLock)
            {
                //cancel running XEventStream.
                CancellationTokenSource targetToken;
                if (monitoredCancellationTokenSources.Remove(sessionId, out targetToken))
                {
                    targetToken.Cancel();
                }
                if (this.monitoredSessions.Remove(sessionId, out session) && session.isStreamActive())
                {
                    // Remove streaming status from session
                    session.toggleStreamLock();

                    //remove all viewers for this session
                    List<string> viewerIds;
                    if (sessionViewers.Remove(sessionId, out viewerIds))
                    {
                        foreach (String viewerId in viewerIds)
                        {
                            this.allViewers.Remove(viewerId);
                        }
                        return true;
                    }
                    else
                    {
                        session = null;
                        return false;
                    }
                }
                else
                {
                    session = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// The core queue processing method
        /// </summary>
        /// <param name="state"></param>
        private void ProcessStreams()
        {
            while (true)
            {
                lock (this.sessionsLock)
                {
                    foreach (var id in this.monitoredSessions.Keys)
                    {
                        ProfilerSession session;
                        this.monitoredSessions.TryGetValue(id, out session);
                        if (!session.isStreamActive())
                        {
                            ProcessStream(id, session);
                            session.toggleStreamLock();
                        }
                    }
                }
            }
        }

        internal async Task HandleXEvent(IXEvent xEvent, ProfilerSession session)
        {
            ProfilerEvent profileEvent = new ProfilerEvent(xEvent.Name, xEvent.Timestamp.ToString());
            foreach (var kvp in xEvent.Fields)
            {
                profileEvent.Values.Add(kvp.Key, kvp.Value.ToString());
            }
            foreach (var kvp in xEvent.Actions)
            {
                profileEvent.Values.Add(kvp.Key, kvp.Value.ToString());
            }
            var eventList = new List<ProfilerEvent>();
            eventList.Add(profileEvent);
            var eventsLost = session.EventsLost;

            if (eventList.Count > 0 || eventsLost)
            {
                session.FilterOldEvents(eventList);
                eventList = session.FilterProfilerEvents(eventList);
                // notify all viewers of the event.
                List<string> viewerIds = this.sessionViewers[session.XEventSession.Id];

                foreach (string viewerId in viewerIds)
                {
                    if (allViewers[viewerId].active)
                    {
                        SendEventsToListeners(viewerId, eventList, eventsLost);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a stream for new XEvents if the session hasn't streamed yet.
        /// </summary>
        private void ProcessStream(int id, ProfilerSession session)
        {
            CancellationTokenSource threadCancellationToken = new CancellationTokenSource();
            var connectionString = ConnectionService.BuildConnectionString(session.ConnectionInfo.ConnectionDetails, true);
            var eventStreamer = new XELiveEventStreamer(connectionString, (session.XEventSession as XEventSession).Session?.Name);
            var task = eventStreamer.ReadEventStream(xEvent => HandleXEvent(xEvent, session), threadCancellationToken.Token);

            task.ContinueWith(t =>
            {
                //If cancellation token is missing, that means stream was stopped normally, do not fire error in this case.
                CancellationTokenSource targetToken;
                if (monitoredCancellationTokenSources.TryGetValue(id, out targetToken))
                {
                    SendStoppedSessionInfoToListeners(session.XEventSession.Id);
                    ProfilerSession tempSession;
                    RemoveSession(session.XEventSession.Id, out tempSession);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);

            this.monitoredCancellationTokenSources.Add(id, threadCancellationToken);
        }

        private List<ProfilerEvent> PollSession(ProfilerSession session)
        {
            var events = new List<ProfilerEvent>();
            try
            {
                if (session == null || session.XEventSession == null)
                {
                    return events;
                }

                var targetXml = session.XEventSession.GetTargetXml();

                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(targetXml);

                var nodes = xmlDoc.DocumentElement.GetElementsByTagName("event");
                foreach (XmlNode node in nodes)
                {
                    var profilerEvent = ParseProfilerEvent(node);
                    if (profilerEvent != null)
                    {
                        events.Add(profilerEvent);
                    }
                }
            }
            catch (XEventException)
            {
                SendStoppedSessionInfoToListeners(session.XEventSession.Id);
                ProfilerSession tempSession;
                RemoveSession(session.XEventSession.Id, out tempSession);
            }
            catch (Exception ex)
            {
                Logger.Write(TraceEventType.Warning, "Failed to poll session. error: " + ex.Message);
            }

            session.FilterOldEvents(events);
            return session.FilterProfilerEvents(events);
        }

        /// <summary>
        /// Notify listeners about closed sessions
        /// </summary>
        private void SendStoppedSessionInfoToListeners(int sessionId)
        {
            lock (listenersLock)
            {
                foreach (var listener in this.listeners)
                {
                    foreach (string viewerId in sessionViewers[sessionId])
                    {
                        listener.SessionStopped(viewerId, sessionId);
                    }
                }
            }
        }

        /// <summary>
        /// Notify listeners when new profiler events are available
        /// </summary>
        private void SendEventsToListeners(string sessionId, List<ProfilerEvent> events, bool eventsLost)
        {
            lock (listenersLock)
            {
                foreach (var listener in this.listeners)
                {
                    listener.EventsAvailable(sessionId, events, eventsLost);
                }
            }
        }

        /// <summary>
        /// Parse a single event node from XEvent XML
        /// </summary>
        private ProfilerEvent ParseProfilerEvent(XmlNode node)
        {
            var name = node.Attributes["name"];
            var timestamp = node.Attributes["timestamp"];

            var profilerEvent = new ProfilerEvent(name.InnerText, timestamp.InnerText);

            foreach (XmlNode childNode in node.ChildNodes)
            {
                var childName = childNode.Attributes["name"];
                XmlNode typeNode = childNode.SelectSingleNode("type");
                var typeName = typeNode.Attributes["name"];
                XmlNode valueNode = childNode.SelectSingleNode("value");

                if (!profilerEvent.Values.ContainsKey(childName.InnerText))
                {
                    profilerEvent.Values.Add(childName.InnerText, valueNode.InnerText);
                }
            }

            return profilerEvent;
        }
    }
}
