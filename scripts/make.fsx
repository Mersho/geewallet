#!/usr/bin/env fsharpi

open System
open System.IO
open System.Diagnostics

#r "System.Configuration"
#load "InfraLib/Misc.fs"
#load "InfraLib/Process.fs"
#load "InfraLib/Git.fs"
open FSX.Infrastructure
open Process

let DEFAULT_FRONTEND = "GWallet.Frontend.Console"
let BACKEND = "GWallet.Backend"

type BinaryConfig =
    | Debug
    | Release
    override self.ToString() =
        sprintf "%A" self

let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let buildConfigFileName = "build.config"
let buildConfigContents =
    let buildConfig = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, buildConfigFileName))
    if not (buildConfig.Exists) then
        Console.Error.WriteLine "ERROR: configure hasn't been run yet, run ./configure.sh first"
        Environment.Exit 1

    let skipBlankLines line = not <| String.IsNullOrWhiteSpace line
    let splitLineIntoKeyValueTuple (line:string) =
        let pair = line.Split([|'='|], StringSplitOptions.RemoveEmptyEntries)
        if pair.Length <> 2 then
            failwithf "All lines in %s must conform to format:\n\tkey=value"
                      buildConfigFileName
        pair.[0], pair.[1]

    let buildConfigContents =
        File.ReadAllLines buildConfig.FullName
        |> Array.filter skipBlankLines
        |> Array.map splitLineIntoKeyValueTuple
        |> Map.ofArray
    buildConfigContents

let GetOrExplain key map =
    match map |> Map.tryFind key with
    | Some k -> k
    | None   -> failwithf "No entry exists in %s with a key '%s'."
                          buildConfigFileName key

let prefix = buildConfigContents |> GetOrExplain "Prefix"
let libInstallPath = DirectoryInfo (Path.Combine (prefix, "lib", "gwallet"))
let binInstallPath = DirectoryInfo (Path.Combine (prefix, "bin"))

let launcherScriptPath = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, "bin", "gwallet"))
let mainBinariesPath binaryConfig = DirectoryInfo (Path.Combine(__SOURCE_DIRECTORY__,
                                                                "..",
                                                                "src",
                                                                DEFAULT_FRONTEND,
                                                                "bin",
                                                                binaryConfig.ToString()))

let wrapperScript = """#!/bin/sh
set -e
exec mono "$TARGET_DIR/$GWALLET_PROJECT.exe" "$@"
"""

let rootDir = DirectoryInfo(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let PrintNugetVersion () =
    let nugetExe = Path.Combine(rootDir.FullName, ".nuget", "nuget.exe") |> FileInfo
    if not (nugetExe.Exists) then
        false
    else
        let nugetProc = Process.Execute ({ Command = "mono"; Arguments = nugetExe.FullName }, Echo.Off)
        Console.WriteLine nugetProc.Output.StdOut
        if nugetProc.ExitCode = 0 then
            true
        else
            Console.Error.WriteLine nugetProc.Output.StdErr
            Console.WriteLine()
            failwith "nuget process' output contained errors ^"

let JustBuild binaryConfig =
    let buildTool = Map.tryFind "BuildTool" buildConfigContents
    if buildTool.IsNone then
        failwith "A BuildTool should have been chosen by the configure script, please report this bug"

    Console.WriteLine (sprintf "Building gwallet in %s mode..." (binaryConfig.ToString()))
    let configOption = sprintf "/p:Configuration=%s" (binaryConfig.ToString())
    let configOptions =
        match buildConfigContents |> Map.tryFind "DefineConstants" with
        | Some constants -> sprintf "%s;DefineConstants=%s" configOption constants
        | None   -> configOption
    let buildProcess = Process.Execute ({ Command = buildTool.Value; Arguments = configOptions }, Echo.All)
    if (buildProcess.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s build failed" buildTool.Value)
        PrintNugetVersion() |> ignore
        Environment.Exit 1

    Directory.CreateDirectory(launcherScriptPath.Directory.FullName) |> ignore
    let wrapperScriptWithPaths =
        wrapperScript.Replace("$TARGET_DIR", libInstallPath.FullName)
                     .Replace("$GWALLET_PROJECT", DEFAULT_FRONTEND)
    File.WriteAllText (launcherScriptPath.FullName, wrapperScriptWithPaths)

let MakeCheckCommand (commandName: string) =
    if not (Process.CommandWorksInShell commandName) then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" commandName)
        Environment.Exit 1

let GetPathToFrontendBinariesDir (binaryConfig: BinaryConfig) =
    Path.Combine (rootDir.FullName, "src", DEFAULT_FRONTEND, "bin", binaryConfig.ToString())

let GetPathToBackend () =
    Path.Combine (rootDir.FullName, "src", BACKEND)

let MakeAll() =
    let buildConfig = BinaryConfig.Debug
    JustBuild buildConfig
    buildConfig

let RunFrontend (buildConfig: BinaryConfig) (maybeArgs: Option<string>) =
    let oldVersionOfMono =
        let versionOfMonoWhereRunningExesDirectlyIsSupported = "5.16"

        match Misc.GuessPlatform() with
        | Misc.Platform.Windows ->
            // not using Mono anyway
            false
        | Misc.Platform.Mac ->
            // unlikely that anyone uses old Mono versions in Mac, as it's easy to update (TODO: detect anyway)
            false
        | Misc.Platform.Linux ->
            let pkgConfig = "pkg-config"
            if not (Process.CommandWorksInShell pkgConfig) then
                failwithf "'%s' was uninstalled after ./configure.sh was invoked?" pkgConfig
            let pkgConfigCmd = { Command = pkgConfig
                                 Arguments = sprintf "--atleast-version=%s mono"
                                                 versionOfMonoWhereRunningExesDirectlyIsSupported }
            let processResult = Process.Execute(pkgConfigCmd, Echo.OutputOnly)
            processResult.ExitCode <> 0

    let pathToFrontend = Path.Combine(GetPathToFrontendBinariesDir buildConfig, DEFAULT_FRONTEND + ".exe")

    let fileName, finalArgs =
        if oldVersionOfMono then
            match maybeArgs with
            | None | Some "" -> "mono",pathToFrontend
            | Some args -> "mono",pathToFrontend + " " + args
        else
            match maybeArgs with
            | None | Some "" -> pathToFrontend,String.Empty
            | Some args -> pathToFrontend,args

    let startInfo = ProcessStartInfo(FileName = fileName, Arguments = finalArgs, UseShellExecute = false)
    startInfo.EnvironmentVariables.["MONO_ENV_OPTIONS"] <- "--debug"

    let proc = Process.Start startInfo
    proc.WaitForExit()
    proc

let maybeTarget = GatherTarget (Misc.FsxArguments(), None)
match maybeTarget with
| None ->
    MakeAll() |> ignore

| Some("release") ->
    JustBuild BinaryConfig.Release

| Some "nuget" ->
    Console.WriteLine "This target is for debugging purposes."

    if not (PrintNugetVersion()) then
        Console.Error.WriteLine "Nuget executable has not been downloaded yet, try `make` alone first"
        Environment.Exit 1

| Some("zip") ->
    let zipCommand = "zip"
    MakeCheckCommand zipCommand

    let version = Misc.GetCurrentVersion(rootDir).ToString()

    let release = BinaryConfig.Release
    JustBuild release
    let binDir = "bin"
    Directory.CreateDirectory(binDir) |> ignore

    let zipNameWithoutExtension = sprintf "gwallet.v.%s" version
    let zipName = sprintf "%s.zip" zipNameWithoutExtension
    let pathToZip = Path.Combine(binDir, zipName)
    if (File.Exists (pathToZip)) then
        File.Delete (pathToZip)

    let pathToFolderToBeZipped = Path.Combine(binDir, zipNameWithoutExtension)
    if (Directory.Exists (pathToFolderToBeZipped)) then
        Directory.Delete (pathToFolderToBeZipped, true)

    let pathToFrontend = GetPathToFrontendBinariesDir release
    let zipRun = Process.Execute({ Command = "cp"
                                   Arguments = sprintf "-rfvp %s %s" pathToFrontend pathToFolderToBeZipped },
                                 Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "Precopy for ZIP compression failed"
        Environment.Exit 1

    let previousCurrentDir = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory binDir
    let zipLaunch = { Command = zipCommand
                      Arguments = sprintf "%s -r %s %s"
                                      zipCommand zipName zipNameWithoutExtension }
    let zipRun = Process.Execute(zipLaunch, Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "ZIP compression failed"
        Environment.Exit 1
    Directory.SetCurrentDirectory previousCurrentDir

| Some("check") ->
    Console.WriteLine "Running tests..."
    Console.WriteLine ()

    let nunitCommand = "nunit-console"
    MakeCheckCommand nunitCommand
    let testAssembly = "GWallet.Backend.Tests"
    let testAssemblyPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "src", testAssembly, "bin",
                                        testAssembly + ".dll")
    if not (File.Exists(testAssemblyPath)) then
        failwithf "File not found: %s" testAssemblyPath
    let nunitRun = Process.Execute({ Command = nunitCommand; Arguments = testAssemblyPath },
                                   Echo.All)
    if (nunitRun.ExitCode <> 0) then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

| Some("install") ->
    let buildConfig = BinaryConfig.Release
    JustBuild buildConfig

    Console.WriteLine "Installing gwallet..."
    Console.WriteLine ()
    Misc.CopyDirectoryRecursively (mainBinariesPath buildConfig, libInstallPath, [])

    let finalPrefixPathOfWrapperScript = FileInfo (Path.Combine(binInstallPath.FullName, launcherScriptPath.Name))
    if not (Directory.Exists(finalPrefixPathOfWrapperScript.Directory.FullName)) then
        Directory.CreateDirectory(finalPrefixPathOfWrapperScript.Directory.FullName) |> ignore
    File.Copy(launcherScriptPath.FullName, finalPrefixPathOfWrapperScript.FullName, true)
    if Process.Execute({ Command = "chmod"; Arguments = sprintf "ugo+x %s" finalPrefixPathOfWrapperScript.FullName },
                        Echo.Off).ExitCode <> 0 then
        failwith "Unexpected chmod failure, please report this bug"

| Some("run") ->
    let buildConfig = MakeAll()
    RunFrontend buildConfig None
        |> ignore

| Some "update-servers" ->
    let buildConfig = MakeAll()
    Directory.SetCurrentDirectory (GetPathToBackend())
    let proc1 = RunFrontend buildConfig (Some "--update-servers-file")
    if proc1.ExitCode <> 0 then
        Environment.Exit proc1.ExitCode
    else
        let proc2 = RunFrontend buildConfig (Some "--update-servers-stats")
        Environment.Exit proc2.ExitCode

| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2
