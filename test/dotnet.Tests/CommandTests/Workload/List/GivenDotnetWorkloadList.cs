// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.CommandLine;
using ManifestReaderTests;
using Microsoft.DotNet.Cli.Commands;
using Microsoft.DotNet.Cli.Commands.Workload.List;
using Microsoft.NET.Sdk.WorkloadManifestReader;

namespace Microsoft.DotNet.Cli.Workload.List.Tests
{
    public class GivenDotnetWorkloadList : SdkTest
    {
        private readonly ParseResult _machineReadableParseResult;
        private readonly ParseResult _parseResult;
        private readonly BufferedReporter _reporter;
        private readonly string _manifestPath;

        public GivenDotnetWorkloadList(ITestOutputHelper log) : base(log)
        {
            _reporter = new BufferedReporter();
            _machineReadableParseResult = Parser.Parse("dotnet workload list --machine-readable");
            _parseResult = Parser.Parse("dotnet workload list");
            _manifestPath = Path.Combine(_testAssetsManager.GetAndValidateTestProjectDirectory("SampleManifest"), "MockListSample.json");
        }

        [Fact]
        public void GivenNoWorkloadsAreInstalledListIsEmpty()
        {
            _reporter.Clear();
            var expectedWorkloads = new List<WorkloadId>();
            var workloadInstaller = new MockWorkloadRecordRepo(expectedWorkloads);
            var command = new WorkloadListCommand(_parseResult, _reporter, workloadInstaller, "6.0.100");
            command.Execute();

            // Expected number of lines for table headers
            // Expecting a workload set adds two lines
            _reporter.Lines.Count.Should().Be(8);
        }

        [WindowsOnlyFact]
        public void GivenAvailableWorkloadsItCanComputeVisualStudioIds()
        {
            var workloadResolver = WorkloadResolver.CreateForTests(new MockManifestProvider(("SampleManifest", _manifestPath, "5.0.0", "6.0.100")), Directory.GetCurrentDirectory());

#pragma warning disable CA1416 // Validate platform compatibility
            var availableWorkloads = VisualStudioWorkloads.GetAvailableVisualStudioWorkloads(workloadResolver);
            availableWorkloads.Should().Contain("mock.workload.1", "mock-workload-1");
            availableWorkloads.Should().Contain("Microsoft.NET.Component.mock.workload.1", "mock-workload-1");
#pragma warning restore CA1416 // Validate platform compatibility
        }

        [Fact]
        public void GivenNoWorkloadsAreInstalledMachineReadableListIsEmpty()
        {
            _reporter.Clear();
            var expectedWorkloads = new List<WorkloadId>();
            var workloadInstaller = new MockWorkloadRecordRepo(expectedWorkloads);
            var command = new WorkloadListCommand(_machineReadableParseResult, _reporter, workloadInstaller, "6.0.100");
            command.Execute();

            _reporter.Lines.Should().Contain(l => l.Contains(@"""installed"":[]"));
        }

        [Fact]
        public void GivenWorkloadsAreInstalledListIsNotEmpty()
        {
            _reporter.Clear();
            var expectedWorkloads = new List<WorkloadId>() { new WorkloadId("mock-workload-1"), new WorkloadId("mock-workload-2"), new WorkloadId("mock-workload-3") };
            var workloadInstaller = new MockWorkloadRecordRepo(expectedWorkloads);
            var workloadResolver = WorkloadResolver.CreateForTests(new MockManifestProvider(("SampleManifest", _manifestPath, "5.0.0", "6.0.100")), Directory.GetCurrentDirectory());
            var command = new WorkloadListCommand(_parseResult, _reporter, workloadInstaller, "6.0.100", workloadResolver: workloadResolver);
            command.Execute();

            foreach (var workload in expectedWorkloads)
            {
                _reporter.Lines.Select(line => line.Trim()).Should().Contain($"{workload}            5.0.0/6.0.100         SDK 6.0.100");
            }
        }

        [Fact]
        public void GivenWorkloadsAreInstalledMachineReadableListIsNotEmpty()
        {
            _reporter.Clear();
            var expectedWorkloads = new List<WorkloadId>() { new WorkloadId("mock-workload-1"), new WorkloadId("mock-workload-2"), new WorkloadId("mock-workload-3") };
            var workloadInstaller = new MockWorkloadRecordRepo(expectedWorkloads);
            var command = new WorkloadListCommand(_machineReadableParseResult, _reporter, workloadInstaller, "6.0.100");
            command.Execute();

            _reporter.Lines.Should().Contain(l => l.Contains("{\"installed\":[\"mock-workload-1\",\"mock-workload-2\",\"mock-workload-3\"]"));
        }

        [Fact]
        public void GivenWorkloadsAreOutOfDateUpdatesAreAdvertised()
        {
            _reporter.Clear();
            var testDirectory = _testAssetsManager.CreateTestDirectory().Path;
            var expectedWorkloads = new List<WorkloadId>() { new WorkloadId("mock-workload-1"), new WorkloadId("mock-workload-2"), new WorkloadId("mock-workload-3") };
            var workloadInstaller = new MockWorkloadRecordRepo(expectedWorkloads);
            var workloadResolver = WorkloadResolver.CreateForTests(new MockManifestProvider(new[] { _manifestPath }), testDirectory);

            // Lay out fake advertising manifests with pack version update for pack A (in workloads 1 and 3)
            var userProfileDir = Path.Combine(testDirectory, "user-profile");
            var manifestPath = Path.Combine(userProfileDir, "sdk-advertising", "6.0.100", "SampleManifest", "WorkloadManifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            File.Copy(Path.Combine(_testAssetsManager.GetAndValidateTestProjectDirectory("SampleManifest"), "MockListSampleUpdated.json"), manifestPath);

            var command = new WorkloadListCommand(_parseResult, _reporter, workloadInstaller, "6.0.100", workloadResolver: workloadResolver, userProfileDir: userProfileDir);
            command.Execute();

            // Workloads 1 and 3 should have updates
            _reporter.Lines.Should().Contain(string.Format(CliCommandStrings.WorkloadListWorkloadUpdatesAvailable, "mock-workload-1 mock-workload-3"));
        }
    }
}
