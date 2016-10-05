﻿using Extensibility;
using Microsoft.Vbe.Interop;
using Ninject;
using Ninject.Extensions.Factory;
using Rubberduck.Root;
using Rubberduck.UI;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using Ninject.Extensions.Interception;
using NLog;
using Rubberduck.Settings;
using Rubberduck.SettingsProvider;

namespace Rubberduck
{
    /// <remarks>
    /// Special thanks to Carlos Quintero (MZ-Tools) for providing the general structure here.
    /// </remarks>
    [ComVisible(true)]
    [Guid(ClassId)]
    [ProgId(ProgId)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    // ReSharper disable once InconsistentNaming // note: underscore prefix hides class from COM API
    public class _Extension : IDTExtensibility2
    {
        private const string ClassId = "8D052AD8-BBD2-4C59-8DEC-F697CA1F8A66";
        private const string ProgId = "Rubberduck.Extension";

        private VBEditor.SafeComWrappers.VBA.VBE _ide;
        private VBEditor.SafeComWrappers.VBA.AddIn _addin;
        private bool _isInitialized;
        private bool _isBeginShutdownExecuted;

        private IKernel _kernel;
        private App _app;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public void OnAddInsUpdate(ref Array custom) { }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public void OnConnection(object Application, ext_ConnectMode ConnectMode, object AddInInst, ref Array custom)
        {
            try
            {
                _ide = new VBEditor.SafeComWrappers.VBA.VBE((VBE)Application);
                _addin = new VBEditor.SafeComWrappers.VBA.AddIn((AddIn)AddInInst);
                _addin.Object = this;

                switch (ConnectMode)
                {
                    case ext_ConnectMode.ext_cm_Startup:
                        // normal execution path - don't initialize just yet, wait for OnStartupComplete to be called by the host.
                        break;
                    case ext_ConnectMode.ext_cm_AfterStartup:
                        InitializeAddIn();
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        Assembly LoadFromSameFolder(object sender, ResolveEventArgs args)
        {
            var folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var assemblyPath = Path.Combine(folderPath, new AssemblyName(args.Name).Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                return null;
            }

            var assembly = Assembly.LoadFile(assemblyPath);
            return assembly;
        }

        public void OnStartupComplete(ref Array custom)
        {
            InitializeAddIn();
        }

        public void OnBeginShutdown(ref Array custom)
        {
            _isBeginShutdownExecuted = true;
            ShutdownAddIn();
        }

        // ReSharper disable InconsistentNaming
        public void OnDisconnection(ext_DisconnectMode RemoveMode, ref Array custom)
        {
            switch (RemoveMode)
            {
                case ext_DisconnectMode.ext_dm_UserClosed:
                    ShutdownAddIn();
                    break;

                case ext_DisconnectMode.ext_dm_HostShutdown:
                    if (_isBeginShutdownExecuted)
                    {
                        // this is the normal case: nothing to do here, we already ran ShutdownAddIn.
                    }
                    else
                    {
                        // some hosts do not call OnBeginShutdown: this mitigates it.
                        ShutdownAddIn();
                    }
                    break;
            }
        }

        private void InitializeAddIn()
        {
            if (_isInitialized)
            {
                // The add-in is already initialized. See:
                // The strange case of the add-in initialized twice
                // http://msmvps.com/blogs/carlosq/archive/2013/02/14/the-strange-case-of-the-add-in-initialized-twice.aspx
                return;
            }

            _kernel = new StandardKernel(new NinjectSettings { LoadExtensions = true }, new FuncModule(), new DynamicProxyModule());

            try
            {
                var currentDomain = AppDomain.CurrentDomain;
                currentDomain.AssemblyResolve += LoadFromSameFolder;

                var config = new XmlPersistanceService<GeneralSettings>
                {
                    FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rubberduck", "rubberduck.config")
                };

                var settings = config.Load(null);
                if (settings != null)
                {
                    try
                    {
                        var cultureInfo = CultureInfo.GetCultureInfo(settings.Language.Code);
                        Dispatcher.CurrentDispatcher.Thread.CurrentUICulture = cultureInfo;
                    }
                    catch (CultureNotFoundException) { }
                }

                _kernel.Load(new RubberduckModule(_ide, _addin));

                _app = _kernel.Get<App>();
                _app.Startup();
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _logger.Fatal(exception);
                System.Windows.Forms.MessageBox.Show(exception.ToString(), RubberduckUI.RubberduckLoadFailure, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShutdownAddIn()
        {
            if (_app != null)
            {
                _app.Shutdown();
                _app = null;
            }

            if (_kernel != null)
            {
                _kernel.Dispose();
                _kernel = null;
            }

            _ide.Release();
            _isInitialized = false;
        }
    }
}
