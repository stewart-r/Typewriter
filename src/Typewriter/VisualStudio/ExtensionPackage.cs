﻿using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using Typewriter.Generation.Controllers;
using Typewriter.Metadata.CodeDom;
using Typewriter.Metadata.Providers;
using Typewriter.VisualStudio.ContextMenu;

namespace Typewriter.VisualStudio
{
    [ProvideOptionPage(typeof(TypewriterOptionsPage), "Typewriter", "General", 101, 106, true)]
    [Guid(Constants.ExtensionPackageId)]
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string)]
    [InstalledProductRegistration("#110", "#112", "1.11.0", IconResourceID = 401)]
    [ProvideLanguageService(typeof(LanguageService), Constants.LanguageName, 100, DefaultToInsertSpaces = true)]
    [ProvideLanguageExtension(typeof(LanguageService), Constants.TemplateExtension)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class ExtensionPackage : Package, IDisposable
    {
        private DTE dte;
        private Log log;
        private IVsStatusbar statusBar;
        private SolutionMonitor solutionMonitor;
        private TemplateController templateController;
        private IEventQueue eventQueue;
        private IMetadataProvider metadataProvider;
        private GenerationController generationController;

        /// <summary>
        /// This read-only property returns the package instance.
        /// </summary>
        internal static ExtensionPackage Instance { get; private set; }

        public TypewriterOptionsPage Options => (TypewriterOptionsPage) GetDialogPage(typeof (TypewriterOptionsPage));

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            base.Initialize();

            GetDte();
            GetStatusbar();
            GetCodeModelProvider();
            RegisterLanguageService();
            RegisterIcons();
            ClearTempDirectory();
            
            
            this.solutionMonitor = new SolutionMonitor();
            this.templateController = new TemplateController(dte);
            this.eventQueue = new EventQueue(statusBar);
            this.generationController = new GenerationController(dte, metadataProvider, templateController, eventQueue);
            RenderTemplate.Initialize(this);

            WireupEvents();
            ErrorList.Initialize(this);

            Instance = this;
        }

        private void WireupEvents()
        {
            solutionMonitor.ProjectAdded += (sender, args) => templateController.ResetTemplates();
            solutionMonitor.ProjectRemoved += (sender, args) => templateController.ResetTemplates();

            solutionMonitor.TemplateAdded += (sender, args) => templateController.ResetTemplates();
            solutionMonitor.TemplateDeleted += (sender, args) => templateController.ResetTemplates();
            solutionMonitor.TemplateRenamed += (sender, args) => templateController.ResetTemplates();
            solutionMonitor.TemplateChanged += (sender, args) => generationController.OnTemplateChanged(args.Path);


            solutionMonitor.CsFileAdded += (sender, args) => generationController.OnCsFileChanged(args.Paths);
            solutionMonitor.CsFileChanged += (sender, args) => generationController.OnCsFileChanged(args.Paths);

            solutionMonitor.CsFileDeleted += (sender, args) => generationController.OnCsFileDeleted(args.Paths);


            solutionMonitor.CsFileRenamed += (sender, args) => generationController.OnCsFileRenamed(args.Paths, args.OldPaths);

            RenderTemplate.Instance.RenderTemplateClicked += (sender, args) => generationController.OnTemplateChanged(args.Path, true);
        }

        private void GetDte()
        {
            this.dte = GetService(typeof(DTE)) as DTE;
            this.log = new Log(dte);

            if (this.dte == null)
                ErrorHandler.ThrowOnFailure(1);
        }
        
        private void GetStatusbar()
        {
            this.statusBar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;

            if (this.statusBar == null)
                ErrorHandler.ThrowOnFailure(1);
        }

        private void GetCodeModelProvider()
        {
            try
            {
                var version = GetVisualStudioVersion();
                Log.Debug($"Visual Studio Version: {version}");

                if (version.Major >= 14 || version.Major == 0)
                {
                    var assembly = Assembly.LoadFrom(Path.Combine(Constants.TypewriterDirectory, "Typewriter.Metadata.Roslyn.dll"));
                    var type = assembly.GetType("Typewriter.Metadata.Roslyn.RoslynMetadataProvider");
                    var provider = (IMetadataProvider) Activator.CreateInstance(type);

                    Log.Debug("Using Roslyn");
                    Constants.RoslynEnabled = true;
                    this.metadataProvider = provider;

                    return;
                }
            }
            catch(Exception exception)
            {
                Log.Debug(exception.Message);
            }

            Log.Debug("Using CodeDom");
            this.metadataProvider = new CodeDomMetadataProvider(this.dte);
        }

        private static Version GetVisualStudioVersion()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "msenv.dll");

                if (File.Exists(path))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(path);
                    var productVersion = versionInfo.ProductVersion;

                    for (var i = 0; i < productVersion.Length; i++)
                    {
                        if (char.IsDigit(productVersion, i) == false && productVersion[i] != '.')
                        {
                            productVersion = productVersion.Substring(0, i);
                            break;
                        }
                    }

                    return new Version(productVersion);
                }
            }
            catch (Exception exception)
            {
                Log.Debug(exception.Message);
            }

            return new Version(0, 0); // Not running inside Visual Studio!
        }

        private void RegisterLanguageService()
        {
            var languageService = new LanguageService();
            ((IServiceContainer)this).AddService(typeof(LanguageService), languageService, true);
        }

        private static void RegisterIcons()
        {
            try
            {
                var icon = ThemeInfo.IsDark ? "dark.ico" : "light.ico";
                var path = Path.Combine(Constants.ResourcesDirectory, icon);

                using (RegistryKey classes = Registry.CurrentUser.OpenSubKey("SoftWare\\Classes", true))
                {
                    if (classes == null) return;

                    using (var key = classes.CreateSubKey(Constants.TemplateExtension + "\\DefaultIcon"))
                    {
                        key?.SetValue(string.Empty, path);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug("Failed to register icons: {0}", e.Message);
            }
        }

        private static void ClearTempDirectory()
        {
            try
            {
                if (Directory.Exists(Constants.TempDirectory))
                {
                    Directory.Delete(Constants.TempDirectory, true);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            if (this.eventQueue != null)
            {
                this.eventQueue.Dispose();
                this.eventQueue = null;
            }

            ExtensionPackage.Instance = null;
        }
    }
}