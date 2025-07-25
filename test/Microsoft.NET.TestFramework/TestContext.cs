﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.NET.TestFramework
{
    public class TestContext
    {
        //  Generally the folder the test DLL is in
        private string? _testExecutionDirectory;

        public string TestExecutionDirectory
        {
            get
            {
                if (_testExecutionDirectory == null)
                {
                    throw new InvalidOperationException("TestExecutionDirectory should never be null.");
                }
                return _testExecutionDirectory;
            }
            set
            {
                _testExecutionDirectory = value;
            }
        }

        private string? _testAssetsDirectory;

        public string TestAssetsDirectory
        {
            get
            {
                if (_testAssetsDirectory == null)
                {
                    throw new InvalidOperationException("TestAssetsDirectory should never be null.");
                }
                return _testAssetsDirectory;
            }
            set
            {
                _testAssetsDirectory = value;
            }
        }

        public string? TestPackages { get; set; }

        //  For tests which want the global packages folder isolated in the repo, but
        //  can share it with other tests
        public string? TestGlobalPackagesFolder { get; set; }

        public string? NuGetCachePath { get; set; }

        public string? NuGetFallbackFolder { get; set; }

        public string? NuGetExePath { get; set; }

        public string? SdkVersion { get; set; }

        private ToolsetInfo? _toolsetUnderTest;

        public ToolsetInfo ToolsetUnderTest
        {
            get
            {
                if (_toolsetUnderTest == null)
                {
                    throw new InvalidOperationException("ToolsetUnderTest should never be null.");
                }
                return _toolsetUnderTest;
            }
            set
            {
                _toolsetUnderTest = value;
            }
        }

        private static TestContext? _current;

        public static TestContext Current
        {
            get
            {
                if (_current == null)
                {
                    //  Initialize test context in cases where it hasn't been initialized via the entry point
                    //  (ie when using test explorer or another runner)
                    Initialize(TestCommandLine.Parse(Array.Empty<string>()));
                }
                return _current ?? throw new InvalidOperationException("TestContext.Current should never be null.");
            }
            set
            {
                _current = value;
            }
        }

        public const string LatestRuntimePatchForNetCoreApp2_0 = "2.0.9";

        public static string GetRuntimeGraphFilePath()
        {
            string dotnetRoot = TestContext.Current.ToolsetUnderTest.DotNetRoot;

            DirectoryInfo sdksDir = new(Path.Combine(dotnetRoot, "sdk"));

            var lastWrittenSdk = sdksDir.EnumerateDirectories().OrderByDescending(di => di.LastWriteTime).First();

            return lastWrittenSdk.GetFiles("RuntimeIdentifierGraph.json").Single().FullName;
        }

        public void AddTestEnvironmentVariables(IDictionary<string, string> environment)
        {
            environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            //  Set NUGET_PACKAGES environment variable to match value from build.ps1
            if(NuGetCachePath is not null)
            {
                environment["NUGET_PACKAGES"] = NuGetCachePath;
            }

            environment["GenerateResourceMSBuildArchitecture"] = "CurrentArchitecture";
            environment["GenerateResourceMSBuildRuntime"] = "CurrentRuntime";

            //  Prevent test MSBuild nodes from persisting
            environment["MSBUILDDISABLENODEREUSE"] = "1";

            ToolsetUnderTest.AddTestEnvironmentVariables(environment);
        }


        public static void Initialize(TestCommandLine commandLine)
        {
            //  Show verbose debugging output for tests
            CommandLoggingContext.SetVerbose(true);
            Reporter.Reset();

            foreach (var (name, value) in commandLine.EnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Environment.SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0");

            //  Reset this environment variable so that if the dotnet under test is different than the
            //  one running the tests, it won't interfere
            Environment.SetEnvironmentVariable("MSBuildSdksPath", null);

            TestContext testContext = new();

            bool runAsTool = false;
            if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "TestAssets")))
            {
                runAsTool = true;
                testContext.TestAssetsDirectory = Path.Combine(AppContext.BaseDirectory, "TestAssets");
            }
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_SDK_TEST_AS_TOOL")))
            {
                //  Pretend to run as a tool, but use the test assets found in the repo
                //  This allows testing most of the "tests as global tool" behavior by setting an environment
                //  variable instead of packing the test, and installing it as a global tool.
                runAsTool = true;
                string? FindFolder = FindFolderInTree(Path.Combine("test", "TestAssets"), AppContext.BaseDirectory);
                if (FindFolder is not null)
                {
                    testContext.TestAssetsDirectory = FindFolder;
                }
            }
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_SDK_TEST_ASSETS_DIRECTORY")) && Environment.GetEnvironmentVariable("DOTNET_SDK_TEST_ASSETS_DIRECTORY") is not null)
            {
                testContext.TestAssetsDirectory = Environment.GetEnvironmentVariable("DOTNET_SDK_TEST_ASSETS_DIRECTORY")!;
            }

            string? repoRoot = null;
#if DEBUG
            string repoConfiguration = "Debug";
#else
            string repoConfiguration = "Release";
#endif

            if (commandLine.SDKRepoPath != null)
            {
                repoRoot = commandLine.SDKRepoPath;
            }
            else if (!commandLine.NoRepoInference && !runAsTool)
            {
                repoRoot = GetRepoRoot();
            }

            if (!string.IsNullOrEmpty(commandLine.TestExecutionDirectory) && commandLine.TestExecutionDirectory is not null)
            {
                testContext.TestExecutionDirectory = commandLine.TestExecutionDirectory;
            }
            else if (Environment.GetEnvironmentVariable("DOTNET_SDK_TEST_EXECUTION_DIRECTORY") != null)
            {
                testContext.TestExecutionDirectory = Environment.GetEnvironmentVariable("DOTNET_SDK_TEST_EXECUTION_DIRECTORY")!;
            }
            else if (runAsTool)
            {
                testContext.TestExecutionDirectory = Path.Combine(Path.GetTempPath(), "dotnetSdkTests", Path.GetRandomFileName());
            }
            else
            {
                string? FindFolder1 = FindFolderInTree("artifacts", AppContext.BaseDirectory);
                string? FindFolder2 = FindFolderInTree(Path.Combine("test", "TestAssets"), AppContext.BaseDirectory);
                if (FindFolder1 is not null)
                {
                    testContext.TestExecutionDirectory = Path.Combine(FindFolder1, "tmp", repoConfiguration, "testing");
                }
                if (FindFolder2 is not null)
                {
                    testContext.TestAssetsDirectory = FindFolder2;
                }
            }

            Directory.CreateDirectory(testContext.TestExecutionDirectory);

            string? artifactsDir = Environment.GetEnvironmentVariable("DOTNET_SDK_ARTIFACTS_DIR");
            if (string.IsNullOrEmpty(artifactsDir) && !string.IsNullOrEmpty(repoRoot))
            {
                artifactsDir = Path.Combine(repoRoot, "artifacts");
            }

            if (!string.IsNullOrEmpty(artifactsDir))
            {
                testContext.TestGlobalPackagesFolder = Path.Combine(artifactsDir, ".nuget", "packages");
            }
            else
            {
                testContext.TestGlobalPackagesFolder = Path.Combine(testContext.TestExecutionDirectory, ".nuget", "packages");
            }

            if (repoRoot != null && artifactsDir is not null)
            {
                testContext.NuGetFallbackFolder = Path.Combine(artifactsDir, ".nuget", "NuGetFallbackFolder");
                testContext.NuGetExePath = Path.Combine(artifactsDir, ".nuget", $"nuget{Constants.ExeSuffix}");
                testContext.NuGetCachePath = Path.Combine(artifactsDir, ".nuget", "packages");

                testContext.TestPackages = Path.Combine(artifactsDir, "tmp", repoConfiguration, "testing", "testpackages");
            }
            else if (runAsTool)
            {
                testContext.NuGetFallbackFolder = Path.Combine(testContext.TestExecutionDirectory, ".nuget", "NuGetFallbackFolder");
                testContext.NuGetExePath = Path.Combine(testContext.TestExecutionDirectory, ".nuget", $"nuget{Constants.ExeSuffix}");
                testContext.NuGetCachePath = Path.Combine(testContext.TestExecutionDirectory, ".nuget", "packages");

                var testPackages = Path.Combine(testContext.TestExecutionDirectory, "Testpackages");
                if (Directory.Exists(testPackages))
                {
                    testContext.TestPackages = testPackages;
                }
            }
            else
            {
                var nugetFolder = FindFolderInTree(".nuget", AppContext.BaseDirectory, false)
                    ?? Path.Combine(testContext.TestExecutionDirectory, ".nuget");

                testContext.NuGetFallbackFolder = Path.Combine(nugetFolder, "NuGetFallbackFolder");
                testContext.NuGetExePath = Path.Combine(nugetFolder, $"nuget{Constants.ExeSuffix}");
                testContext.NuGetCachePath = Path.Combine(nugetFolder, "packages");

                var testPackages = Path.Combine(testContext.TestExecutionDirectory, "Testpackages");
                if (Directory.Exists(testPackages))
                {
                    testContext.TestPackages = testPackages;
                }
            }

            if (commandLine.SdkVersion != null)
            {
                testContext.SdkVersion = commandLine.SdkVersion;
            }

            testContext.ToolsetUnderTest = ToolsetInfo.Create(repoRoot, artifactsDir, repoConfiguration, commandLine);

            //  Important to set this before below code which ends up calling through TestContext.Current, which would
            //  result in infinite recursion / stack overflow if TestContext.Current wasn't set
            Current = testContext;

            //  Set up test hooks for in-process tests
            Environment.SetEnvironmentVariable(
                Constants.MSBUILD_EXE_PATH,
                Path.Combine(testContext.ToolsetUnderTest.SdkFolderUnderTest, "MSBuild.dll"));

            Environment.SetEnvironmentVariable(
                "MSBuildSDKsPath",
                Path.Combine(testContext.ToolsetUnderTest.SdksPath));

#if NETCOREAPP
            MSBuildForwardingAppWithoutLogging.MSBuildExtensionsPathTestHook =
                testContext.ToolsetUnderTest.SdkFolderUnderTest;
#endif
        }

        public static string? GetRepoRoot()
        {
            string? directory = AppContext.BaseDirectory;

            while (directory is not null)
            {
                var gitPath = Path.Combine(directory, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    // Found the repo root, which should either have a .git folder or, if the repo
                    // is part of a Git worktree, a .git file.
                    return directory;
                }

                directory = Directory.GetParent(directory)?.FullName;
            }

            return null;
        }
        private static string FindOrCreateFolderInTree(string relativePath, string startPath)
        {
            string? ret = FindFolderInTree(relativePath, startPath, throwIfNotFound: false);
            if (ret != null)
            {
                return ret;
            }
            ret = Path.Combine(startPath, relativePath);
            Directory.CreateDirectory(ret);
            return ret;
        }
        private static string? FindFolderInTree(string relativePath, string startPath, bool throwIfNotFound = true)
        {
            string currentPath = startPath;
            while (true)
            {
                string path = Path.Combine(currentPath, relativePath);
                if (Directory.Exists(path))
                {
                    return path;
                }
                var parent = Directory.GetParent(currentPath);
                if (parent == null)
                {
                    if (throwIfNotFound)
                    {
                        throw new FileNotFoundException($"Could not find folder '{relativePath}' in '{startPath}' or any of its ancestors");
                    }
                    else
                    {
                        return null;
                    }
                }
                currentPath = parent.FullName;
            }
        }

        public void WriteGlobalJson(string path)
        {
            WriteGlobalJson(path, SdkVersion);
        }

        public static void WriteGlobalJson(string path, string? sdkVersion)
        {
            if (!string.IsNullOrEmpty(sdkVersion))
            {
                string globalJsonPath = Path.Combine(path, "global.json");
                File.WriteAllText(globalJsonPath, @"{
  ""sdk"": {
    ""version"": """ + sdkVersion + @"""
  }
}");
            }
        }

        public static bool IsLocalized()
        {
            for (var culture = CultureInfo.CurrentUICulture; !culture.Equals(CultureInfo.InvariantCulture); culture = culture.Parent)
            {
                if (culture.Name == "en")
                {
                    return false;
                }
            }

            return true;
        }
    }
}
