﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using RoslynPad.Roslyn.Completion.Providers;
using RoslynPad.Utilities;
using IPackageSourceProvider = NuGet.Configuration.IPackageSourceProvider;
using PackageSource = NuGet.Configuration.PackageSource;
using PackageSourceProvider = NuGet.Configuration.PackageSourceProvider;
using Settings = NuGet.Configuration.Settings;

namespace RoslynPad.UI
{
    [Export, Export(typeof(INuGetCompletionProvider)), Shared]
    public sealed class NuGetViewModel : NotificationObject, INuGetCompletionProvider
    {
        private const int MaxSearchResults = 50;

        private readonly CommandLineSourceRepositoryProvider _sourceRepositoryProvider;
        private readonly ExceptionDispatchInfo _initializationException;
        private readonly IEnumerable<string> _configFilePaths;
        private readonly IEnumerable<PackageSource> _packageSources;

        public string GlobalPackageFolder { get; }

#pragma warning disable CS8618 // Non-nullable field is uninitialized.
        [ImportingConstructor]
        public NuGetViewModel(ITelemetryProvider telemetryProvider)
#pragma warning restore CS8618 // Non-nullable field is uninitialized.
        {
            try
            {
                ISettings settings;

                try
                {
                    settings = Settings.LoadDefaultSettings(
                        root: null,
                        configFileName: null,
                        machineWideSettings: new XPlatMachineWideSetting());
                }
                catch (NuGetConfigurationException ex)
                {
                    telemetryProvider.ReportError(ex);

                    // create default settings using a non-existent config file
                    settings = new Settings(nameof(RoslynPad));
                }

                GlobalPackageFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
                _configFilePaths = SettingsUtility.GetConfigFilePaths(settings);
                _packageSources = SettingsUtility.GetEnabledSources(settings);

                DefaultCredentialServiceUtility.SetupDefaultCredentialService(NullLogger.Instance, nonInteractive: false);

                var sourceProvider = new PackageSourceProvider(settings);
                _sourceRepositoryProvider = new CommandLineSourceRepositoryProvider(sourceProvider);
            }
            catch (Exception e)
            {
                _initializationException = ExceptionDispatchInfo.Capture(e);
            }
        }

        public async Task<IReadOnlyList<PackageData>> GetPackagesAsync(string searchTerm, bool includePrerelease, bool exactMatch, CancellationToken cancellationToken)
        {
            _initializationException?.Throw();

            var filter = new SearchFilter(includePrerelease);

            foreach (var sourceRepository in _sourceRepositoryProvider.GetRepositories())
            {
                IPackageSearchMetadata[]? result;
                try
                {
                    result = await sourceRepository.SearchAsync(searchTerm, filter, MaxSearchResults, cancellationToken).ConfigureAwait(false);
                }
                catch (FatalProtocolException)
                {
                    continue;
                }

                if (exactMatch)
                {
                    var match = result.FirstOrDefault(c => string.Equals(c.Identity.Id, searchTerm,
                        StringComparison.OrdinalIgnoreCase));
                    result = match != null ? new[] { match } : null;
                }

                if (result?.Length > 0)
                {
                    var packages = result.Select(x => new PackageData(x)).ToArray();
                    await Task.WhenAll(packages.Select(x => x.Initialize())).ConfigureAwait(false);
                    return packages;
                }
            }

            return Array.Empty<PackageData>();
        }

        async Task<IReadOnlyList<INuGetPackage>> INuGetCompletionProvider.SearchPackagesAsync(string searchString, bool exactMatch, CancellationToken cancellationToken)
        {
            var packages = await GetPackagesAsync(searchString, includePrerelease: true, exactMatch, cancellationToken);
            return packages;
        }

        internal static (List<string> compile, List<string> runtime, List<string> analyzers) ReadProjectLockJson(JObject obj, string packagesDirectory, string framework)
        {
            var compile = new List<string>();
            var compileAssemblies = new HashSet<string>();
            var runtime = new List<string>();
            var runtimeAssemblies = new HashSet<string>();

            var targets = (JObject)obj["targets"];
            foreach (var target in targets)
            {
                if (target.Key == framework)
                {
                    foreach (var package in (JObject)target.Value)
                    {
                        var path = obj["libraries"][package.Key]["path"].Value<string>();
                        var packageRoot = Path.Combine(packagesDirectory, path);
                        if (ReadLockFileSection(packageRoot, package.Value, compile, compileAssemblies, nameof(compile)))
                        {
                            ReadLockFileSection(packageRoot, package.Value, runtime, runtimeAssemblies, nameof(runtime));
                        }
                    }

                    break;
                }
            }

            var analyzers = new List<string>();

            var libraries = (JObject)obj["libraries"];
            foreach (var library in libraries)
            {
                foreach (var item in (JArray)library.Value["files"])
                {
                    var file = item.Value<string>();
                    if (file.StartsWith("analyzers/dotnet/cs/", StringComparison.Ordinal))
                    {
                        var path = Path.Combine(packagesDirectory, library.Value["path"].Value<string>(), file);
                        analyzers.Add(path);
                    }
                }
            }

            return (compile, runtime, analyzers);
        }

        private static bool ReadLockFileSection(string packageRoot, JToken root, List<string> items, HashSet<string> names, string sectionName)
        {
            var section = (JObject)((JObject)root)[sectionName];
            if (section == null)
            {
                return false;
            }

            foreach (var item in section)
            {
                var relativePath = item.Key;
                // Ignore placeholder "_._" files.
                if (IsPlaceholder(relativePath))
                {
                    return false;
                }

                var name = Path.GetFileNameWithoutExtension(relativePath);
                // poor man's conflict resolution ;) take the first one
                if (names.Add(name))
                {
                    items.Add(Path.Combine(packageRoot, relativePath));
                }
            }

            return true;
        }

        internal static bool IsPlaceholder(string relativePath)
        {
            var name = Path.GetFileName(relativePath);
            bool isPlacehlder = string.Equals(name, "_._", StringComparison.InvariantCulture);
            return isPlacehlder;
        }

        internal static JObject LoadJson(TextReader reader)
        {
            JObject obj;

            using (var jsonReader = new JsonTextReader(reader))
            {
                obj = JObject.Load(jsonReader);
            }

            return obj;
        }

        #region Inner Classes

        private class CommandLineSourceRepositoryProvider : ISourceRepositoryProvider
        {
            private readonly List<Lazy<INuGetResourceProvider>> _resourceProviders;
            private readonly List<SourceRepository> _repositories;

            // There should only be one instance of the source repository for each package source.
            private static readonly ConcurrentDictionary<PackageSource, SourceRepository> _cachedSources
                = new ConcurrentDictionary<PackageSource, SourceRepository>();

            public CommandLineSourceRepositoryProvider(IPackageSourceProvider packageSourceProvider)
            {
                PackageSourceProvider = packageSourceProvider;

                _resourceProviders = new List<Lazy<INuGetResourceProvider>>();
                _resourceProviders.AddRange(Repository.Provider.GetCoreV3());

                // Create repositories
                _repositories = PackageSourceProvider.LoadPackageSources()
                    .Where(s => s.IsEnabled)
                    .Select(CreateRepository)
                    .ToList();
            }

            public IEnumerable<SourceRepository> GetRepositories()
            {
                return _repositories;
            }

            public SourceRepository CreateRepository(PackageSource source)
            {
                return _cachedSources.GetOrAdd(source, new SourceRepository(source, _resourceProviders));
            }

            public SourceRepository CreateRepository(PackageSource source, FeedType type)
            {
                return _cachedSources.GetOrAdd(source, new SourceRepository(source, _resourceProviders, type));
            }

            public IPackageSourceProvider PackageSourceProvider { get; }
        }

        #endregion
    }

    [Export]
    public sealed class NuGetDocumentViewModel : NotificationObject
    {
        private readonly NuGetViewModel _nuGetViewModel;
        private readonly ITelemetryProvider _telemetryProvider;

        private string _searchTerm;
        private bool _isSearching;
        private CancellationTokenSource _searchCts;
        private bool _isPackagesMenuOpen;
        private bool _prerelease;
        private IReadOnlyList<PackageData> _packages;

        public IReadOnlyList<PackageData> Packages
        {
            get => _packages;
            private set => SetProperty(ref _packages, value);
        }

        [ImportingConstructor]
#pragma warning disable CS8618 // Non-nullable field is uninitialized.
        public NuGetDocumentViewModel(NuGetViewModel nuGetViewModel, ICommandProvider commands, ITelemetryProvider telemetryProvider)
#pragma warning restore CS8618 // Non-nullable field is uninitialized.
        {
            _nuGetViewModel = nuGetViewModel;
            _telemetryProvider = telemetryProvider;

            InstallPackageCommand = commands.Create<PackageData>(InstallPackage);
        }

        private void InstallPackage(PackageData package)
        {
            OnPackageInstalled(package);
        }

        public IDelegateCommand<PackageData> InstallPackageCommand { get; }

        private void OnPackageInstalled(PackageData package)
        {
            PackageInstalled?.Invoke(package);
        }

        public event Action<PackageData> PackageInstalled;

        public bool IsSearching
        {
            get => _isSearching;
            private set => SetProperty(ref _isSearching, value);
        }

        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                if (SetProperty(ref _searchTerm, value))
                {
                    PerformSearch();
                }
            }
        }

        public bool IsPackagesMenuOpen
        {
            get => _isPackagesMenuOpen;
            set => SetProperty(ref _isPackagesMenuOpen, value);
        }

        public bool ExactMatch { get; set; }

        public bool Prerelease
        {
            get => _prerelease;
            set
            {
                if (SetProperty(ref _prerelease, value))
                {
                    PerformSearch();
                }
            }
        }

        private void PerformSearch()
        {
            _searchCts?.Cancel();
            var searchCts = new CancellationTokenSource();
            var cancellationToken = searchCts.Token;
            _searchCts = searchCts;

            Task.Run(() => PerformSearch(SearchTerm, cancellationToken), cancellationToken);
        }

        private async Task PerformSearch(string searchTerm, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                Packages = Array.Empty<PackageData>();
                IsPackagesMenuOpen = false;
                return;
            }

            IsSearching = true;
            try
            {
                try
                {
                    var packages = await Task.Run(() =>
                            _nuGetViewModel.GetPackagesAsync(searchTerm, includePrerelease: Prerelease,
                                exactMatch: ExactMatch, cancellationToken: cancellationToken), cancellationToken)
                        .ConfigureAwait(true);

                    Packages = packages;
                    IsPackagesMenuOpen = Packages.Count > 0;
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    _telemetryProvider.ReportError(e);
                }
            }
            finally
            {
                IsSearching = false;
            }
        }
    }

    public sealed class PackageData : INuGetPackage
    {
        private readonly IPackageSearchMetadata? _package;

        private PackageData(string id, NuGetVersion version)
        {
            Id = id;
            Version = version;
        }

        public string Id { get; }
        public NuGetVersion Version { get; }
        public ImmutableArray<PackageData> OtherVersions { get; private set; }

        IEnumerable<string> INuGetPackage.Versions
        {
            get
            {
                if (!OtherVersions.IsDefaultOrEmpty)
                {
                    var lastStable = OtherVersions.FirstOrDefault(v => !v.Version.IsPrerelease);
                    if (lastStable != null)
                    {
                        yield return lastStable.Version.ToString();
                    }

                    foreach (var version in OtherVersions)
                    {
                        if (version != lastStable)
                        {
                            yield return version.Version.ToString();
                        }
                    }
                }
            }
        }

        public PackageData(IPackageSearchMetadata package)
        {
            _package = package;
            Id = package.Identity.Id;
            Version = package.Identity.Version;
        }

        public async Task Initialize()
        {
            if (_package == null) return;
            var versions = await _package.GetVersionsAsync().ConfigureAwait(false);
            OtherVersions = versions.Select(x => new PackageData(Id, x.Version)).OrderByDescending(x => x.Version).ToImmutableArray();
        }
    }

    internal static class SourceRepositoryExtensions
    {
        public static async Task<IPackageSearchMetadata[]> SearchAsync(this SourceRepository sourceRepository, string searchText, SearchFilter searchFilter, int pageSize, CancellationToken cancellationToken)
        {
            var searchResource = await sourceRepository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);

            if (searchResource != null)
            {
                var searchResults = await searchResource.SearchAsync(
                    searchText,
                    searchFilter,
                    0,
                    pageSize,
                    NullLogger.Instance,
                    cancellationToken).ConfigureAwait(false);

                if (searchResults != null)
                {
                    return searchResults.ToArray();
                }
            }

            return Array.Empty<IPackageSearchMetadata>();
        }
    }
}