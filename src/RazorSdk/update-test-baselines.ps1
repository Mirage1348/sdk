param([switch] $Validate)
$RepoRoot= Resolve-Path "$PSScriptRoot/../.."

$TestProjects = "Microsoft.NET.Sdk.Razor.Tests", "Microsoft.NET.Sdk.BlazorWebAssembly.Tests" |
 ForEach-Object { Join-Path -Path "$RepoRoot/test/" -ChildPath $_ };

if($Validate){
  $TestProjects | ForEach-Object { dotnet test --no-build -c Release -l "console;verbosity=normal" $_ --filter AspNetCore=BaselineTest }
}else {
  $TestProjects | ForEach-Object { dotnet test --no-build -c Release -l "console;verbosity=normal" $_ -e ASPNETCORE_TEST_BASELINES=true --filter AspNetCore=BaselineTest }
}
