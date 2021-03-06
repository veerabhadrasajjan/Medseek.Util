﻿namespace Medseek.Util.MicroServices.Host
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using Medseek.Util.Ioc;
    using Medseek.Util.Logging;
    using Medseek.Util.Messaging;

    /// <summary>
    /// Provides the functionality for hosting micro-services defined in an
    /// external .NET assembly.
    /// </summary>
    public class MicroServiceHost : IDisposable
    {
        private readonly List<IInstallable> installables = new List<IInstallable> { UtilComponents.Framework };
        private readonly IocBootstrapper iocBootstrapper;
        private readonly ILog log;

        /// <summary>
        /// Initializes a new instance of the <see cref="MicroServiceHost" /> 
        /// class.
        /// </summary>
        [SuppressMessage("StyleCop.CSharp.NamingRules", "SA1305:FieldNamesMustNotUseHungarianNotation", Justification = "Message Queue is not hugarian notation.")]
        public MicroServiceHost(IIocPlugin ioc, ILoggingPlugin logging, IMqPlugin mqPlugin, params Assembly[] assembliesToInstall)
        {
            if (ioc == null)
                throw new ArgumentNullException("ioc");
            if (logging == null)
                throw new ArgumentNullException("logging");
            if (mqPlugin == null)
                throw new ArgumentNullException("mqPlugin");

            log = logging.GetLogManager().GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
            log.InfoFormat("Creating micro-service host; Plugins = {0}, {1}, {2}, AssembliesToInstall = {3}.", ioc, logging, mqPlugin, string.Join(", ", assembliesToInstall.Select(x => x.GetName().Name)));
            iocBootstrapper = new IocBootstrapper(ioc);
            installables.Add(ioc);
            installables.Add(logging);
            installables.Add(mqPlugin);
            installables.AddRange(
                AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(x => x.GetCustomAttributes<ReferencePluginAttribute>())
                    .Select(x => x.GetType())
                    .Select(x => x.Assembly)
                    .Concat(assembliesToInstall)
                    .Distinct()
                    .SelectMany(x => Installables.FromAssembly(x)));
        }

        /// <summary>
        /// Disposes the micro-service host.
        /// </summary>
        public void Dispose()
        {
            log.Info("Disposing micro-service host.");
            iocBootstrapper.Dispose();
        }

        /// <summary>
        /// Used to enter the main micro-service hosting loop.
        /// </summary>
        public void Start()
        {
            log.Info("Starting micro-service host.");

            iocBootstrapper.Install(installables);
        }
    }
}