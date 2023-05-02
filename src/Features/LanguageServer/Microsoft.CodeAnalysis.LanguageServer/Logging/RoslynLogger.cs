﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging
{
    internal class RoslynLogger : ILogger
    {
        private static RoslynLogger? _instance;
        private static readonly ConcurrentDictionary<FunctionId, string> s_eventMap = new();
        private static readonly ConcurrentDictionary<(FunctionId id, string name), string> s_propertyMap = new();

        private readonly ConcurrentDictionary<int, object> _pendingScopes = new(concurrencyLevel: 2, capacity: 10);
        private static ITelemetryReporter? _telemetryReporter;

        private RoslynLogger()
        {
        }

        public static void Initialize(ITelemetryReporter? reporter, string? telemetryLevel)
        {
            Contract.ThrowIfTrue(_instance is not null);

            FatalError.Handler = ReportFault;
            FatalError.CopyHandlerTo(typeof(Compilation).Assembly);

            if (reporter is not null && telemetryLevel is not null)
            {
                reporter.InitializeSession(telemetryLevel, isDefaultSession: true);
                _telemetryReporter = reporter;
            }

            _instance = new();

            var currentLogger = Logger.GetLogger();
            if (currentLogger is null)
            {
                Logger.SetLogger(_instance);
            }
            else
            {
                Logger.SetLogger(AggregateLogger.Create(currentLogger, _instance));
            }
        }

        private static void ReportFault(Exception exception, ErrorSeverity severity, bool forceDump)
        {
            try
            {
                if (exception is OperationCanceledException { InnerException: { } oceInnerException })
                {
                    ReportFault(oceInnerException, severity, forceDump);
                    return;
                }

                if (exception is AggregateException aggregateException)
                {
                    // We (potentially) have multiple exceptions; let's just report each of them
                    foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                        ReportFault(innerException, severity, forceDump);

                    return;
                }

                if (_telemetryReporter is not null)
                {
                    var eventName = GetEventName(FunctionId.NonFatalWatson);
                    var description = GetDescription(exception);
                    var currentProcess = Process.GetCurrentProcess();
                    _telemetryReporter.ReportFault(eventName, description, (int)severity, forceDump, currentProcess.Id, exception);
                }
            }
            catch (OutOfMemoryException)
            {
                FailFast.OnFatalException(exception);
            }
            catch (Exception e)
            {
                FailFast.OnFatalException(e);
            }
        }

        public bool IsEnabled(FunctionId functionId)
            => _telemetryReporter is not null;

        public void Log(FunctionId functionId, LogMessage logMessage)
        {
            var name = GetEventName(functionId);
            var properties = GetProperties(functionId, logMessage, delta: null);

            try
            {
                _telemetryReporter?.Log(name, properties);
            }
            catch
            {
            }
        }

        public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int blockId, CancellationToken cancellationToken)
        {
            var eventName = GetEventName(functionId);
            var kind = GetKind(logMessage);

            try
            {
                _telemetryReporter?.LogBlockStart(eventName, (int)kind, blockId);
            }
            catch
            {
            }
        }

        public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int blockId, int delta, CancellationToken cancellationToken)
        {
            var properties = GetProperties(functionId, logMessage, delta);
            try
            {
                _telemetryReporter?.LogBlockEnd(blockId, properties, cancellationToken);
            }
            catch
            {
            }
        }

        private static string GetDescription(Exception exception)
        {
            const string CodeAnalysisNamespace = nameof(Microsoft) + "." + nameof(CodeAnalysis);

            // Be resilient to failing here.  If we can't get a suitable name, just fall back to the standard name we
            // used to report.
            try
            {
                // walk up the stack looking for the first call from a type that isn't in the ErrorReporting namespace.
                var frames = new StackTrace(exception).GetFrames();

                // On the .NET Framework, GetFrames() can return null even though it's not documented as such.
                // At least one case here is if the exception's stack trace itself is null.
                if (frames != null)
                {
                    foreach (var frame in frames)
                    {
                        var method = frame?.GetMethod();
                        var methodName = method?.Name;
                        if (methodName == null)
                            continue;

                        var declaringTypeName = method?.DeclaringType?.FullName;
                        if (declaringTypeName == null)
                            continue;

                        if (!declaringTypeName.StartsWith(CodeAnalysisNamespace))
                            continue;

                        return declaringTypeName + "." + methodName;
                    }
                }
            }
            catch
            {
            }

            // If we couldn't get a stack, do this
            return exception.Message;
        }

        private const string EventPrefix = "vs/ide/vbcs/";
        private const string PropertyPrefix = "vs.ide.vbcs.";

        private static string GetEventName(FunctionId id)
            => s_eventMap.GetOrAdd(id, id => EventPrefix + GetTelemetryName(id, separator: '/'));

        private static string GetPropertyName(FunctionId id, string name)
            => s_propertyMap.GetOrAdd((id, name), key => PropertyPrefix + GetTelemetryName(id, separator: '.') + "." + key.name.ToLowerInvariant());

        private static string GetTelemetryName(FunctionId id, char separator)
                => Enum.GetName(typeof(FunctionId), id)!.Replace('_', separator).ToLowerInvariant();

        private static LogType GetKind(LogMessage logMessage)
                => logMessage is KeyValueLogMessage kvLogMessage
                                    ? kvLogMessage.Kind
                                    : logMessage.LogLevel switch
                                    {
                                        >= LogLevel.Information => LogType.UserAction,
                                        _ => LogType.Trace
                                    };

        private static ImmutableDictionary<string, object?> GetProperties(FunctionId id, LogMessage logMessage, int? delta)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, object?>();

            if (logMessage is KeyValueLogMessage kvLogMessage)
            {
                foreach (var (name, val) in kvLogMessage.Properties)
                {
                    builder.Add(GetPropertyName(id, name), val);
                }
            }
            else
            {
                builder.Add(GetPropertyName(id, "Message"), logMessage.GetMessage());
            }

            if (delta.HasValue)
            {
                builder.Add(GetPropertyName(id, "Delta"), delta.Value);
            }

            return builder.ToImmutableDictionary();
        }
    }
}
