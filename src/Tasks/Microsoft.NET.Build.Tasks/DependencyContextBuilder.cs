﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Extensions.DependencyModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace Microsoft.NET.Build.Tasks
{
    internal class DependencyContextBuilder
    {
        private readonly SingleProjectInfo _mainProjectInfo;
        private readonly bool _includeRuntimeFileVersions;
        private IEnumerable<ReferenceInfo> _referenceAssemblies;
        private IEnumerable<ReferenceInfo> _directReferences;
        private IEnumerable<ReferenceInfo> _dependencyReferences;
        private Dictionary<string, List<ReferenceInfo>> _compileReferences;
        private Dictionary<string, List<ResolvedFile>> _resolvedNuGetFiles;
        private Dictionary<string, SingleProjectInfo> _referenceProjectInfos;
        private Dictionary<string, List<RuntimePackAssetInfo>> _runtimePackAssets;
        private CompilationOptions _compilationOptions;
        private string _referenceAssembliesPath;
        private Dictionary<PackageIdentity, string> _filteredPackages;
        private bool _includeMainProjectInDepsFile = true;
        private bool _trimLibrariesWithoutAssets = true;
        private readonly Dictionary<string, DependencyLibrary> _dependencyLibraries;
        private readonly Dictionary<string, List<LibraryDependency>> _libraryDependencies;
        private readonly List<string> _mainProjectDependencies;
        private readonly HashSet<PackageIdentity> _packagesToBeFiltered;
        private readonly bool _isFrameworkDependent;
        private readonly string _platformLibrary;
        private readonly string _dotnetFrameworkName;
        private readonly string _runtimeIdentifier;
        private readonly bool _isPortable;
        private readonly HashSet<string> _usedLibraryNames;
        private readonly RuntimeGraph _runtimeGraph;

        private Dictionary<ReferenceInfo, string> _referenceLibraryNames;

        // This resolver is only used for building file names, so that base path is not required.
        private readonly VersionFolderPathResolver _versionFolderPathResolver = new(rootPath: null);

        private const string NetCorePlatformLibrary = "Microsoft.NETCore.App";

        public DependencyContextBuilder(SingleProjectInfo mainProjectInfo, bool includeRuntimeFileVersions, RuntimeGraph runtimeGraph, ProjectContext projectContext, LockFileLookup libraryLookup)
        {
            _mainProjectInfo = mainProjectInfo;
            _includeRuntimeFileVersions = includeRuntimeFileVersions;
            _runtimeGraph = runtimeGraph;

            _dependencyLibraries = projectContext.LockFileTarget.Libraries
                .Select(lockFileTargetLibrary =>
                {
                    var dependencyLibrary = new DependencyLibrary(lockFileTargetLibrary.Name, lockFileTargetLibrary.Version, lockFileTargetLibrary.Type);

                    LockFileLibrary library;
                    if (libraryLookup.TryGetLibrary(lockFileTargetLibrary, out library))
                    {
                        dependencyLibrary.Sha512 = library.Sha512;
                        dependencyLibrary.Path = library.Path;
                        dependencyLibrary.MSBuildProject = library.MSBuildProject;
                    }

                    return dependencyLibrary;
                }).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

            _libraryDependencies = new Dictionary<string, List<LibraryDependency>>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in projectContext.LockFileTarget.Libraries)
            {
                _libraryDependencies[library.Name] = library.Dependencies
                    .Select(d => new LibraryDependency()
                    {
                        Name = d.Id,
                        MinVersion = d.VersionRange.MinVersion
                    }).ToList();
            }

            _mainProjectDependencies = projectContext.GetTopLevelDependencies().ToList();
            _packagesToBeFiltered = projectContext.PackagesToBeFiltered;

            _isFrameworkDependent = projectContext.IsFrameworkDependent;
            _platformLibrary = projectContext.PlatformLibrary?.Name;
            _dotnetFrameworkName = projectContext.LockFileTarget.TargetFramework.DotNetFrameworkName;
            _runtimeIdentifier = projectContext.LockFileTarget.RuntimeIdentifier;
            _isPortable = projectContext.IsPortable;

            _usedLibraryNames = new HashSet<string>(_dependencyLibraries.Keys, StringComparer.OrdinalIgnoreCase);
        }

        public DependencyContextBuilder(
            SingleProjectInfo mainProjectInfo,
            bool includeRuntimeFileVersions,
            ITaskItem[] runtimeFrameworks,
            string runtimeIdentifier,
            bool isSelfContained,
            string platformLibraryName,
            string targetFramework)
        {
            _mainProjectInfo = mainProjectInfo;
            _includeRuntimeFileVersions = includeRuntimeFileVersions;

            _isFrameworkDependent = LockFileExtensions.IsFrameworkDependent(
                runtimeFrameworks,
                isSelfContained,
                runtimeIdentifier,
                string.IsNullOrWhiteSpace(platformLibraryName));

            _isPortable = _isFrameworkDependent && string.IsNullOrEmpty(_runtimeIdentifier);

            if (_isFrameworkDependent != true || _isPortable != true)
            {
                throw new ArgumentException(
                    $"{nameof(DependencyContextBuilder)} Does not support non FrameworkDependent without assetfile. " +
                    $"runtimeFrameworks: {string.Join(",", runtimeFrameworks.Select(r => r.ItemSpec))} " +
                    $"isSelfContained: {isSelfContained} " +
                    $"runtimeIdentifier: {runtimeIdentifier} " +
                    $"platformLibraryName: {platformLibraryName}");
            }

            _platformLibrary = platformLibraryName;

            //  NOTE: This uses the TargetFramework (ie "net5.0") as the deps.json runtimeTarget name, instead of
            //  the TargetFrameworkMoniker (ie ".NETCoreApp,Version=v5.0"), which is normally used.
            //
            //  This constructor should only be used for C++/CLI, and is used because PackageReference isn't
            //  currently supported in that context so there is no assets file to read from.
            //
            //  Using the TargetFramework instead should have minimal impact.
            _dotnetFrameworkName = targetFramework;
            _runtimeIdentifier = runtimeIdentifier;

            _dependencyLibraries = new Dictionary<string, DependencyLibrary>();
            _libraryDependencies = new Dictionary<string, List<LibraryDependency>>();
            _mainProjectDependencies = new List<string>();
            _packagesToBeFiltered = null;

            _usedLibraryNames = new HashSet<string>();
        }

        private bool IncludeCompilationLibraries => _compilationOptions != null;

        private Dictionary<ReferenceInfo, string> ReferenceLibraryNames
        {
            get
            {
                if (_referenceLibraryNames == null)
                {
                    _referenceLibraryNames = new Dictionary<ReferenceInfo, string>();
                }

                return _referenceLibraryNames;
            }
        }

        public DependencyContextBuilder WithReferenceAssemblies(IEnumerable<ReferenceInfo> referenceAssemblies)
        {
            // note: ReferenceAssembly libraries only export compile-time stuff
            // since they assume the runtime library is present already
            _referenceAssemblies = referenceAssemblies;
            return this;
        }

        public DependencyContextBuilder WithDirectReferences(IEnumerable<ReferenceInfo> directReferences)
        {
            _directReferences = directReferences;
            return this;
        }

        public DependencyContextBuilder WithDependencyReferences(IEnumerable<ReferenceInfo> dependencyReferences)
        {
            _dependencyReferences = dependencyReferences;
            return this;
        }

        public DependencyContextBuilder WithCompileReferences(IEnumerable<ReferenceInfo> compileReferences)
        {
            _compileReferences = new Dictionary<string, List<ReferenceInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in compileReferences.GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase))
            {
                _compileReferences.Add(group.Key, group.ToList());
            }

            return this;
        }

        public DependencyContextBuilder WithResolvedNuGetFiles(IEnumerable<ResolvedFile> resolvedNuGetFiles)
        {
            _resolvedNuGetFiles = new Dictionary<string, List<ResolvedFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in resolvedNuGetFiles.GroupBy(f => f.PackageName, StringComparer.OrdinalIgnoreCase))
            {
                _resolvedNuGetFiles.Add(group.Key, group.ToList());
            }

            return this;
        }

        public DependencyContextBuilder WithReferenceProjectInfos(Dictionary<string, SingleProjectInfo> referenceProjectInfos)
        {
            _referenceProjectInfos = referenceProjectInfos;
            return this;
        }

        public DependencyContextBuilder WithMainProjectInDepsFile(bool includeMainProjectInDepsFile)
        {
            _includeMainProjectInDepsFile = includeMainProjectInDepsFile;
            return this;
        }

        public DependencyContextBuilder WithTrimLibrariesWithoutAssets(bool trimLibrariesWithoutAssets)
        {
            _trimLibrariesWithoutAssets = trimLibrariesWithoutAssets;
            return this;
        }

        public DependencyContextBuilder WithRuntimePackAssets(IEnumerable<RuntimePackAssetInfo> runtimePackAssets)
        {
            _runtimePackAssets = new Dictionary<string, List<RuntimePackAssetInfo>>();
            foreach (var runtimePackGroup in runtimePackAssets.GroupBy(a => a.PackageName))
            {
                var dependencyLibrary = new DependencyLibrary("runtimepack." + runtimePackGroup.Key,
                    NuGetVersion.Parse(runtimePackGroup.First().PackageVersion),
                    "runtimepack");

                _dependencyLibraries.Add(dependencyLibrary.Name, dependencyLibrary);

                _runtimePackAssets[dependencyLibrary.Name] = runtimePackGroup.ToList();
            }
            return this;
        }

        public DependencyContextBuilder WithCompilationOptions(CompilationOptions compilationOptions)
        {
            _compilationOptions = compilationOptions;
            return this;
        }

        public DependencyContextBuilder WithReferenceAssembliesPath(string referenceAssembliesPath)
        {
            // if the path is empty, we want to use the original string instead of a single trailing character.
            if (string.IsNullOrEmpty(referenceAssembliesPath) ||
                referenceAssembliesPath[referenceAssembliesPath.Length - 1] == Path.DirectorySeparatorChar)
            {
                _referenceAssembliesPath = referenceAssembliesPath;
            }
            else
            {
                _referenceAssembliesPath = referenceAssembliesPath + Path.DirectorySeparatorChar;
            }

            return this;
        }

        public DependencyContextBuilder WithPackagesThatWereFiltered(Dictionary<PackageIdentity, string> packagesThatWhereFiltered)
        {
            _filteredPackages = packagesThatWhereFiltered;
            return this;
        }

        public DependencyContext Build(string[] userRuntimeAssemblies = null)
        {
            CalculateExcludedLibraries();

            List<ModifiableRuntimeLibrary> runtimeLibraries = new();

            if (_includeMainProjectInDepsFile)
            {
                runtimeLibraries.Add(GetProjectRuntimeLibrary());
            }

            runtimeLibraries.AddRange(GetRuntimePackLibraries());

            foreach (var library in _dependencyLibraries.Values
                .Where(l => !l.ExcludeFromRuntime && l.Type != "runtimepack"))
            {
                var runtimeLibrary = GetRuntimeLibrary(library, userRuntimeAssemblies);
                if (runtimeLibrary != null)
                {
                    runtimeLibraries.Add(runtimeLibrary);
                }
            }

            var directAndDependencyReferences = _directReferences ?? Enumerable.Empty<ReferenceInfo>();
            if (_dependencyReferences != null)
            {
                directAndDependencyReferences = directAndDependencyReferences.Concat(_dependencyReferences);
            }

            foreach (var directReference in directAndDependencyReferences)
            {
                var runtimeLibrary = new ModifiableRuntimeLibrary(new RuntimeLibrary(
                    type: "reference",
                    name: GetReferenceLibraryName(directReference),
                    version: directReference.Version,
                    hash: string.Empty,
                    runtimeAssemblyGroups: [new RuntimeAssetGroup(string.Empty, [CreateRuntimeFile(directReference.FileName, directReference.FullPath)])],
                    nativeLibraryGroups: [],
                    resourceAssemblies: CreateResourceAssemblies(directReference.ResourceAssemblies),
                    dependencies: [],
                    path: null,
                    hashPath: null,
                    runtimeStoreManifestName: null,
                    serviceable: false));

                runtimeLibraries.Add(runtimeLibrary);
            }

            /*
             * We now need to modify runtimeLibraries to eliminate those that don't have any runtime assets. We follow the following steps:
             * 0. Construct a reverse dependencies list: all runtimeLibraries that depend on this one
             * 1. If runtimeAssemblyGroups, nativeLibraryGroups, dependencies, and resourceAssemblies are all empty, remove this runtimeLibrary as well as any dependencies on it.
             * 2. Add all runtimeLibraries to a list of to-be-processed libraries called libraryCandidatesForRemoval
             * 3. libraryCandidatesForRemoval.Pop() --> if there are no runtimeAssemblyGroups, nativeLibraryGroups, or resourceAssemblies, and either dependencies is empty or all
             *      dependencies have something else that depends on them, remove it (and from libraryCandidatesForRemoval), adding everything that depends on this to 
             *      libraryCandidatesForRemoval if it isn't already there
             * Repeat 3 until libraryCandidatesForRemoval is empty
             */

            // Rather than adding the main project's dependencies, we added references to them, i.e., Foo.Reference.dll
            // instead of Foo.dll. This adds Foo.dll as a reference directly so another reference can be removed if it
            // isn't necessary because of a direct reference from the main project.
            var referenceNameToRealName = new Dictionary<string, string>();
            if (_includeMainProjectInDepsFile)
            {
                var mainProjectReferences = _directReferences;
                if (IncludeCompilationLibraries && _referenceAssemblies != null)
                {
                    if (mainProjectReferences == null)
                    {
                        mainProjectReferences = _referenceAssemblies;
                    }
                    else
                    {
                        mainProjectReferences = mainProjectReferences.Concat(_referenceAssemblies);
                    }
                }

                if (mainProjectReferences != null)
                {
                    foreach (var directReference in mainProjectReferences)
                    {
                        referenceNameToRealName[GetReferenceLibraryName(directReference)] = directReference.Name;
                    }
                }
            }

            var libraries = runtimeLibraries.ToDictionary(lib => lib.Library.Name, lib => lib);
            foreach (var reference in runtimeLibraries)
            {
                foreach (var dependency in reference.Library.Dependencies)
                {
                    var name = referenceNameToRealName.TryGetValue(dependency.Name, out var realName) ? realName : dependency.Name;
                    if (libraries.TryGetValue(name, out var dep))
                    {
                        dep.Dependents.Add(reference.Library.Name);
                    }
                }
            }

            if (_trimLibrariesWithoutAssets)
            {
                var unprocessedLibraries = runtimeLibraries.ToHashSet();
                while (unprocessedLibraries.Any())
                {
                    var lib = unprocessedLibraries.First();
                    unprocessedLibraries.Remove(lib);

                    if (lib.Library.Name.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
                        lib.Library.Name.Equals("xunit.core", StringComparison.OrdinalIgnoreCase))
                    {
                        // Special case xunit and xunit.core, they should not be removed because the xUnit v2 runner looks for these libraries in the deps.json to
                        // identify test projects.
                        // See https://github.com/dotnet/sdk/issues/49248
                        continue;
                    }

                    if (lib.Library.RuntimeAssemblyGroups.Count == 0 && lib.Library.NativeLibraryGroups.Count == 0 && lib.Library.ResourceAssemblies.Count == 0)
                    {
                        if (lib.Library.Dependencies.All(d => !libraries.TryGetValue(d.Name, out var dependency) || dependency.Dependents.Count > 1))
                        {
                            runtimeLibraries.Remove(lib);
                            libraries.Remove(lib.Library.Name);
                            foreach (var dependency in lib.Library.Dependencies)
                            {
                                if (libraries.TryGetValue(dependency.Name, out ModifiableRuntimeLibrary value))
                                {
                                    value.Dependents.Remove(lib.Library.Name);
                                }
                            }

                            foreach (var dependent in lib.Dependents)
                            {
                                if (libraries.TryGetValue(dependent, out var dep))
                                {
                                    unprocessedLibraries.Add(dep);
                                }
                            }
                        }
                    }
                }
            }

            List<CompilationLibrary> compilationLibraries = new();
            if (IncludeCompilationLibraries)
            {
                if (_includeMainProjectInDepsFile)
                {
                    var dependencies = GetProjectDependencies();

                    var projectCompilationLibrary = new CompilationLibrary(
                        type: "project",
                        name: _mainProjectInfo.Name,
                        version: _mainProjectInfo.Version,
                        hash: string.Empty,
                        assemblies: new[] { _mainProjectInfo.OutputName },
                        dependencies: dependencies,
                        serviceable: false);

                    compilationLibraries.Add(projectCompilationLibrary);
                }

                if (_referenceAssemblies != null)
                {
                    foreach (var referenceAssembly in _referenceAssemblies)
                    {
                        string resolvedPath;
                        if (!string.IsNullOrEmpty(_referenceAssembliesPath) &&
                            referenceAssembly.FullPath?.StartsWith(_referenceAssembliesPath) == true)
                        {
                            resolvedPath = referenceAssembly.FullPath.Substring(_referenceAssembliesPath.Length);
                        }
                        else
                        {
                            resolvedPath = Path.GetFileName(referenceAssembly.FullPath);
                        }

                        compilationLibraries.Add(new CompilationLibrary(
                            type: "referenceassembly",
                            name: GetReferenceLibraryName(referenceAssembly),
                            version: referenceAssembly.Version,
                            hash: string.Empty,
                            assemblies: new[] { resolvedPath },
                            dependencies: Enumerable.Empty<Dependency>(),
                            serviceable: false));
                    }
                }

                foreach (var library in _dependencyLibraries.Values
                    .Where(l => !l.ExcludeFromCompilation && l.Type != "runtimepack"))
                {
                    var compilationLibrary = GetCompilationLibrary(library);
                    if (compilationLibrary != null)
                    {
                        compilationLibraries.Add(compilationLibrary);
                    }
                }

                if (_directReferences != null)
                {
                    foreach (var directReference in _directReferences)
                    {
                        compilationLibraries.Add(new CompilationLibrary(
                            type: "reference",
                            name: GetReferenceLibraryName(directReference),
                            version: directReference.Version,
                            hash: string.Empty,
                            assemblies: new[] { directReference.FileName },
                            dependencies: Enumerable.Empty<Dependency>(),
                            serviceable: false));
                    }
                }
            }

            var targetInfo = new TargetInfo(
                _dotnetFrameworkName,
                _runtimeIdentifier,
                runtimeSignature: string.Empty,
                _isPortable);

            // Compute the runtime fallback graph 
            // 
            // If the input RuntimeGraph is empty, or we're not compiling
            // for a specific RID, then an runtime fallback graph is empty
            //
            // Otherwise, it is the set of all runtimes compatible with (inheriting)
            // the target runtime-identifier.
            var runtimeFallbackGraph =
                (_runtimeGraph == null || _runtimeIdentifier == null) ?
                    new RuntimeFallbacks[] { } :
                    _runtimeGraph.Runtimes
                        .Select(runtimeDict => _runtimeGraph.ExpandRuntime(runtimeDict.Key))
                        .Where(expansion => expansion.Contains(_runtimeIdentifier))
                        .Select(expansion => new RuntimeFallbacks(expansion.First(), expansion.Skip(1))); // ExpandRuntime return runtime itself as first item.

            var libraryNames = runtimeLibraries.Select(lib => lib.Library.Name).Concat(compilationLibraries.Select(lib => lib.Name)).ToHashSet();

            return new DependencyContext(
            targetInfo,
            _compilationOptions ?? CompilationOptions.Default,
            compilationLibraries,
            runtimeLibraries.Select(library => new RuntimeLibrary(
                library.Library.Type,
                library.Library.Name,
                library.Library.Version,
                library.Library.Hash,
                library.Library.RuntimeAssemblyGroups,
                library.Library.NativeLibraryGroups,
                library.Library.ResourceAssemblies,
                library.Library.Dependencies.Where(dependency => libraryNames.Contains(dependency.Name)).ToList(),
                library.Library.Serviceable,
                library.Library.Path,
                library.Library.HashPath,
                library.Library.RuntimeStoreManifestName)),
            runtimeFallbackGraph);
        }

        private ModifiableRuntimeLibrary GetProjectRuntimeLibrary()
        {
            RuntimeAssetGroup[] runtimeAssemblyGroups = new[] { new RuntimeAssetGroup(string.Empty, _mainProjectInfo.OutputName) };

            var dependencies = GetProjectDependencies();

            //  Runtime pack assets only get added as dependencies to the runtime (not the compile) project
            if (_runtimePackAssets != null)
            {
                foreach (var runtimePackName in _runtimePackAssets.Keys)
                {
                    dependencies.Add(_dependencyLibraries[runtimePackName].Dependency);
                }
            }

            return new ModifiableRuntimeLibrary(new RuntimeLibrary(
                type: "project",
                name: GetUniqueLibraryName(_mainProjectInfo.Name, "Project"),
                version: _mainProjectInfo.Version,
                hash: string.Empty,
                runtimeAssemblyGroups: runtimeAssemblyGroups,
                nativeLibraryGroups: Array.Empty<RuntimeAssetGroup>(),
                resourceAssemblies: CreateResourceAssemblies(_mainProjectInfo.ResourceAssemblies),
                dependencies: dependencies,
                path: null,
                hashPath: null,
                runtimeStoreManifestName: GetRuntimeStoreManifestName(_mainProjectInfo.Name, _mainProjectInfo.Version),
                serviceable: false));
        }

        private List<Dependency> GetProjectDependencies()
        {
            List<Dependency> dependencies = new();
            foreach (var dependencyName in _mainProjectDependencies)
            {
                if (_dependencyLibraries.TryGetValue(dependencyName, out var dependencyLibrary))
                {
                    //  Include dependency if it would be included either as a runtime or compilation
                    //  (if compilation libraries are being included) library
                    if (!dependencyLibrary.ExcludeFromRuntime ||
                        (IncludeCompilationLibraries && !dependencyLibrary.ExcludeFromCompilation))
                    {
                        dependencies.Add(dependencyLibrary.Dependency);
                    }
                }
            }

            var references = _directReferences;
            if (IncludeCompilationLibraries && _referenceAssemblies != null)
            {
                if (references == null)
                {
                    references = _referenceAssemblies;
                }
                else
                {
                    references = references.Concat(_referenceAssemblies);
                }
            }

            if (references != null)
            {
                foreach (var directReference in references)
                {
                    dependencies.Add(
                        new Dependency(
                            GetReferenceLibraryName(directReference),
                            directReference.Version));
                }
            }

            return dependencies;
        }

        private IEnumerable<ModifiableRuntimeLibrary> GetRuntimePackLibraries()
        {
            if (_runtimePackAssets == null)
            {
                return [];
            }
            return _runtimePackAssets.Select(runtimePack =>
            {
                var runtimeAssemblyGroup = new RuntimeAssetGroup(string.Empty,
                    runtimePack.Value.Where(asset => asset.AssetType == AssetType.Runtime)
                    .Select(asset => CreateRuntimeFile(asset.DestinationSubPath, asset.SourcePath)));

                var nativeLibraryGroup = new RuntimeAssetGroup(string.Empty,
                    runtimePack.Value.Where(asset => asset.AssetType == AssetType.Native)
                    .Select(asset => CreateRuntimeFile(asset.DestinationSubPath, asset.SourcePath)));

                return new ModifiableRuntimeLibrary(new RuntimeLibrary(
                    type: "runtimepack",
                    name: runtimePack.Key,
                    version: runtimePack.Value.First().PackageVersion,
                    hash: string.Empty,
                    runtimeAssemblyGroups: [runtimeAssemblyGroup],
                    nativeLibraryGroups: [nativeLibraryGroup],
                    resourceAssemblies: [],
                    dependencies: [],
                    serviceable: false));
            });
        }

        private ModifiableRuntimeLibrary GetRuntimeLibrary(DependencyLibrary library, string[] userRuntimeAssemblies)
        {
            GetCommonLibraryProperties(library,
                out string hash,
                out HashSet<Dependency> libraryDependencies,
                out bool serviceable,
                out string path,
                out string hashPath,
                out SingleProjectInfo referenceProjectInfo);

            if (referenceProjectInfo is UnreferencedProjectInfo)
            {
                // unreferenced ProjectInfos will be added later as simple dll dependencies
                return null;
            }

            List<RuntimeAssetGroup> runtimeAssemblyGroups = new();
            List<RuntimeAssetGroup> nativeLibraryGroups = new();
            List<ResourceAssembly> resourceAssemblies = new();

            if (library.Type == "project" && !(referenceProjectInfo is UnreferencedProjectInfo))
            {
                var fileName = Path.GetFileNameWithoutExtension(library.Path);
                var assemblyPath = userRuntimeAssemblies?.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(fileName));
                var runtimeFile = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath) ? CreateRuntimeFile(referenceProjectInfo.OutputName, assemblyPath) :
                                  !string.IsNullOrWhiteSpace(library.Path) && File.Exists(library.Path) ? CreateRuntimeFile(referenceProjectInfo.OutputName, library.Path) :
                                  new RuntimeFile(referenceProjectInfo.OutputName, string.Empty, string.Empty);
                runtimeAssemblyGroups.Add(new RuntimeAssetGroup(string.Empty, [runtimeFile]));

                resourceAssemblies.AddRange(referenceProjectInfo.ResourceAssemblies
                                .Select(r => new ResourceAssembly(r.RelativePath, r.Culture)));
            }
            else
            {
                if (_resolvedNuGetFiles != null && _resolvedNuGetFiles.TryGetValue(library.Name, out var resolvedNuGetFiles))
                {
                    var runtimeFiles = resolvedNuGetFiles.Where(f => f.Asset == AssetType.Runtime &&
                                                                !f.IsRuntimeTarget);

                    runtimeAssemblyGroups.Add(new RuntimeAssetGroup(string.Empty,
                                                runtimeFiles.Select(CreateRuntimeFile)));

                    var nativeFiles = resolvedNuGetFiles.Where(f => f.Asset == AssetType.Native &&
                                                                !f.IsRuntimeTarget);

                    nativeLibraryGroups.Add(new RuntimeAssetGroup(string.Empty,
                                                nativeFiles.Select(CreateRuntimeFile)));

                    var resourceFiles = resolvedNuGetFiles.Where(f => f.Asset == AssetType.Resources &&
                                                                !f.IsRuntimeTarget);

                    resourceAssemblies.AddRange(resourceFiles.Select(f => new ResourceAssembly(f.PathInPackage, f.Culture)));

                    var runtimeTargets = resolvedNuGetFiles.Where(f => f.IsRuntimeTarget)
                                                                .GroupBy(f => f.RuntimeIdentifier);

                    foreach (var runtimeIdentifierGroup in runtimeTargets)
                    {
                        var managedRuntimeTargetsFiles = runtimeIdentifierGroup.Where(f => f.Asset == AssetType.Runtime).ToList();
                        if (managedRuntimeTargetsFiles.Any())
                        {
                            runtimeAssemblyGroups.Add(new RuntimeAssetGroup(runtimeIdentifierGroup.Key,
                                                            managedRuntimeTargetsFiles.Select(CreateRuntimeFile)));
                        }

                        var nativeRuntimeTargetsFiles = runtimeIdentifierGroup.Where(f => f.Asset == AssetType.Native).ToList();
                        if (nativeRuntimeTargetsFiles.Any())
                        {
                            nativeLibraryGroups.Add(new RuntimeAssetGroup(runtimeIdentifierGroup.Key,
                                                            nativeRuntimeTargetsFiles.Select(CreateRuntimeFile)));
                        }
                    }
                }

            }

            var runtimeLibrary = new ModifiableRuntimeLibrary(new RuntimeLibrary(
                type: library.Type,
                name: library.Name,
                version: library.Version.ToString(),
                hash: hash,
                runtimeAssemblyGroups: runtimeAssemblyGroups,
                nativeLibraryGroups: nativeLibraryGroups,
                resourceAssemblies: resourceAssemblies,
                dependencies: libraryDependencies,
                path: path,
                hashPath: hashPath,
                runtimeStoreManifestName: GetRuntimeStoreManifestName(library.Name, library.Version.ToString()),
                serviceable: serviceable));

            return runtimeLibrary;
        }

        private CompilationLibrary GetCompilationLibrary(DependencyLibrary library)
        {
            GetCommonLibraryProperties(library,
                out string hash,
                out HashSet<Dependency> libraryDependencies,
                out bool serviceable,
                out string path,
                out string hashPath,
                out SingleProjectInfo referenceProjectInfo);

            List<string> assemblies = new();

            if (library.Type == "project" && !(referenceProjectInfo is UnreferencedProjectInfo))
            {
                assemblies.Add(referenceProjectInfo.OutputName);
            }
            else if (_compileReferences != null && _compileReferences.TryGetValue(library.Name, out var compileReferences))
            {
                foreach (var compileReference in compileReferences)
                {
                    assemblies.Add(compileReference.PathInPackage);
                }
            }

            return new CompilationLibrary(
                type: library.Type,
                name: library.Name,
                version: library.Version.ToString(),
                hash,
                assemblies,
                libraryDependencies,
                serviceable,
                path,
                hashPath);
        }

        private void GetCommonLibraryProperties(DependencyLibrary library,
                    out string hash,
                    out HashSet<Dependency> dependencies,
                    out bool serviceable,
                    out string path,
                    out string hashPath,
                    out SingleProjectInfo referenceProjectInfo)
        {
            serviceable = true;
            referenceProjectInfo = null;

            dependencies = new HashSet<Dependency>();
            List<LibraryDependency> libraryDependencies;
            if (_libraryDependencies.TryGetValue(library.Name, out libraryDependencies))
            {
                foreach (var dependency in libraryDependencies)
                {
                    if (_dependencyLibraries.TryGetValue(dependency.Name, out var libraryDependency))
                    {
                        if (!libraryDependency.ExcludeFromRuntime ||
                            (!libraryDependency.ExcludeFromCompilation && IncludeCompilationLibraries))
                        {
                            dependencies.Add(libraryDependency.Dependency);
                        }
                    }
                }
            }

            hash = string.Empty;
            path = null;
            hashPath = null;
            if (library.Type == "package")
            {
                // TEMPORARY: All packages are serviceable in RC2
                // See https://github.com/dotnet/cli/issues/2569
                serviceable = true;
                if (!string.IsNullOrEmpty(library.Sha512))
                {
                    hash = "sha512-" + library.Sha512;
                    hashPath = _versionFolderPathResolver.GetHashFileName(library.Name, library.Version);
                }

                path = library.Path;
            }
            else if (library.Type == "project")
            {
                serviceable = false;
                referenceProjectInfo = GetProjectInfo(library);

                foreach (var dependencyReference in referenceProjectInfo.DependencyReferences)
                {
                    dependencies.Add(
                        new Dependency(
                            GetReferenceLibraryName(dependencyReference),
                            dependencyReference.Version));
                }
            }
        }

        private RuntimeFile CreateRuntimeFile(ResolvedFile resolvedFile)
        {
            string relativePath = resolvedFile.PathInPackage;
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = resolvedFile.DestinationSubPath;
            }
            return CreateRuntimeFile(relativePath, resolvedFile.SourcePath);
        }

        private RuntimeFile CreateRuntimeFile(string path, string fullPath)
        {
            if (_includeRuntimeFileVersions)
            {
                string fileVersion = FileUtilities.GetFileVersion(fullPath).ToString();
                string assemblyVersion = FileUtilities.TryGetAssemblyVersion(fullPath)?.ToString();
                return new RuntimeFile(path, assemblyVersion, fileVersion);
            }
            else
            {
                return new RuntimeFile(path, null, null);
            }
        }

        private static IEnumerable<ResourceAssembly> CreateResourceAssemblies(IEnumerable<ResourceAssemblyInfo> resourceAssemblyInfos)
        {
            return resourceAssemblyInfos
                .Select(r => new ResourceAssembly(r.RelativePath, r.Culture));
        }

        private SingleProjectInfo GetProjectInfo(DependencyLibrary library)
        {
            string projectPath = library.MSBuildProject;
            if (string.IsNullOrEmpty(projectPath))
            {
                throw new BuildErrorException(Strings.CannotFindProjectInfo, library.Name);
            }

            string mainProjectDirectory = Path.GetDirectoryName(_mainProjectInfo.ProjectPath);
            string fullProjectPath = Path.GetFullPath(Path.Combine(mainProjectDirectory, projectPath));

            SingleProjectInfo referenceProjectInfo = null;
            if (_referenceProjectInfos?.TryGetValue(fullProjectPath, out referenceProjectInfo) != true ||
                referenceProjectInfo == null)
            {
                return UnreferencedProjectInfo.Default;
            }

            return referenceProjectInfo;
        }

        private void CalculateExcludedLibraries()
        {
            Dictionary<string, DependencyLibrary> libraries = _dependencyLibraries;

            HashSet<string> runtimeExclusionList = new(StringComparer.OrdinalIgnoreCase);

            if (_isFrameworkDependent && !string.IsNullOrEmpty(_platformLibrary))
            {
                //  Exclude platform library and dependencies.
                runtimeExclusionList.Add(_platformLibrary);

                Stack<LibraryDependency> dependenciesToWalk = new(_libraryDependencies[_platformLibrary]);

                // If the platform library is not Microsoft.NETCore.App, treat it as an implicit dependency.
                // This makes it so Microsoft.AspNet.* 2.x platforms also exclude Microsoft.NETCore.App files.
                if (!string.Equals(_platformLibrary, NetCorePlatformLibrary, StringComparison.OrdinalIgnoreCase))
                {
                    if (_dependencyLibraries.TryGetValue(NetCorePlatformLibrary, out var netCoreDependencyLibrary))
                    {
                        dependenciesToWalk.Push(new LibraryDependency()
                        {
                            Name = netCoreDependencyLibrary.Name,
                            MinVersion = netCoreDependencyLibrary.Version
                        });
                    }
                }

                while (dependenciesToWalk.Any())
                {
                    var dependency = dependenciesToWalk.Pop();
                    if (runtimeExclusionList.Contains(dependency.Name))
                    {
                        continue;
                    }

                    //  Resolved version of library has to match dependency version exactly, so that we
                    //  don't exclude newer versions of libraries that are part of the platform
                    if (_dependencyLibraries[dependency.Name].Version == dependency.MinVersion)
                    {
                        runtimeExclusionList.Add(dependency.Name);
                        foreach (var newDependency in _libraryDependencies[dependency.Name])
                        {
                            dependenciesToWalk.Push(newDependency);
                        }
                    }
                }
            }

            if (_packagesToBeFiltered != null)
            {
                foreach (var packageToFilter in _packagesToBeFiltered)
                {
                    if (_dependencyLibraries.TryGetValue(packageToFilter.Id, out var library))
                    {
                        if (library.Type == "package" &&
                            _dependencyLibraries[packageToFilter.Id].Version == packageToFilter.Version)
                        {
                            runtimeExclusionList.Add(packageToFilter.Id);
                        }
                    }
                }
            }

            foreach (var packageToExcludeFromRuntime in runtimeExclusionList)
            {
                _dependencyLibraries[packageToExcludeFromRuntime].ExcludeFromRuntime = true;
            }
        }

        private string GetReferenceLibraryName(ReferenceInfo reference)
        {
            if (!ReferenceLibraryNames.TryGetValue(reference, out string name))
            {
                // Reference names can conflict with PackageReference names, so
                // ensure that the Reference names are unique when creating libraries
                name = GetUniqueLibraryName(reference.Name);

                ReferenceLibraryNames.Add(reference, name);
                _usedLibraryNames.Add(name);
            }

            return name;
        }

        private string GetUniqueLibraryName(string name, string qualifier = "Reference")
        {
            if (_usedLibraryNames.Contains(name))
            {
                string startingName = $"{name}.{qualifier}";
                name = startingName;

                int suffix = 1;
                while (_usedLibraryNames.Contains(name))
                {
                    name = $"{startingName}{suffix++}";
                }
            }

            return name;
        }

        private string GetRuntimeStoreManifestName(string packageName, string packageVersion)
        {
            string runtimeStoreManifestName = null;
            if (_filteredPackages != null && _filteredPackages.Any())
            {
                var pkg = new PackageIdentity(packageName, NuGetVersion.Parse(packageVersion));
                _filteredPackages?.TryGetValue(pkg, out runtimeStoreManifestName);
            }
            return runtimeStoreManifestName;
        }

        private class DependencyLibrary
        {
            public string Name { get; }
            public NuGetVersion Version { get; }
            public string Type { get; }
            public Dependency Dependency { get; }
            public string Sha512 { get; set; }
            public string Path { get; set; }
            public string MSBuildProject { get; set; }

            public bool ExcludeFromRuntime { get; set; }

            public bool ExcludeFromCompilation { get; set; }

            public DependencyLibrary(string name, NuGetVersion version, string type)
            {
                Name = name;
                Version = version;
                Type = type;
                Dependency = new Dependency(name, version.ToString());
            }
        }

        private struct LibraryDependency
        {
            public string Name { get; set; }
            public NuGetVersion MinVersion { get; set; }
        }

        private class ModifiableRuntimeLibrary
        {
            // Dependents are assemblies that depend on this library, as opposed to dependencies which are libraries that this one depends on
            public HashSet<string> Dependents { get; set; }
            public RuntimeLibrary Library { get; set; }

            public ModifiableRuntimeLibrary(RuntimeLibrary library)
            {
                this.Dependents = new();
                this.Library = library;
            }
        }
    }
}
