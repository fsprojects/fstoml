module FsToml.Transform.CompilerService

open FsToml
open FsToml.ProjectSystem
open Microsoft.FSharp.Compiler.SourceCodeServices

let getName project =
    project.AssemblyName +
        match project.OutputType with
        | OutputType.Library -> ".dll"
        | _ -> ".exe"


module Configuration =
    open System.IO

    let getOutputPath cfg name =
         defaultArg (cfg.OutputPath |> Option.map (fun p -> Path.Combine(p,name))) (Path.Combine("bin", name))

    let getCompilerParams (target : Target.Target) (name : string) (cfg : Configuration) =

        let debug =
            if cfg.DebugSymbols |> Option.exists id then ":full"
            elif cfg.DebugType.IsSome then
                match cfg.DebugType.Value with
                | DebugType.None -> "-"
                | DebugType.Full -> ":full"
                | DebugType.PdbOnly -> ":pdbonly"
            else "-"

        let platofrm =
            if target.PlatformType = PlatformType.AnyCPU && cfg.Prefer32bit |> Option.exists id then
                "anycpu32bitpreferred"
            else
                target.PlatformType.ToString()

        let outPath = getOutputPath cfg name
        let xmlPath = defaultArg cfg.DocumentationFile (outPath + ".xml")

        [|
            yield "--tailcalls" + if cfg.Tailcalls |> Option.exists id then "+" else "-"
            yield "--warnaserror" + if cfg.WarningsAsErrors |> Option.exists id then "+" else "-"

            if cfg.Constants.IsSome then
                for c in cfg.Constants.Value do
                    yield "-d:" + c
            yield "--debug" + debug
            yield "--optimize" + if cfg.Optimize |> Option.exists id then "+" else "-"
            yield "--platofrm:" + platofrm
            yield "--warn:" + string (defaultArg cfg.WarningLevel 3)
            yield "--out:" + outPath
            yield "--doc:" + xmlPath
            match cfg.NoWarn with
            | None | Some [||] -> ()
            | Some nowarns -> yield "--nowarn:" + (nowarns |> Seq.map (string) |> String.concat ",")
            match cfg.OtherFlags with
            | None | Some [||] -> ()
            | Some flags -> yield! flags
        |]

module References =
    open System
    open System.IO
    open System.Reflection

    let getPath ver nm =
         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) +
         @"\Reference Assemblies\Microsoft\Framework\.NETFramework\" + ver + "\\" + nm + ".dll"

    let sysLib ver nm =
        if Environment.OSVersion.Platform = PlatformID.Win32NT then
            // file references only valid on Windows
            getPath ver nm
        else
            let sysDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            let (++) a b = Path.Combine(a,b)
            sysDir ++ nm + ".dll"

    let fsCore ver =
        if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then
            // file references only valid on Windows
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86) +
            @"\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\" + ver + @"\FSharp.Core.dll"
        else
            sysLib ver "FSharp.Core"

    let dependsOnFacade path  =
        let a = Assembly.LoadFile path
        a.GetReferencedAssemblies()
        |> Array.exists (fun an -> an.Name.Contains "System.Runtime")

    let getFacade ver =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) +
        @"\Reference Assemblies\Microsoft\Framework\.NETFramework\" + ver + "\\Facades\\"
        |> Directory.GetFiles

    let getPathToReference (target : Target.Target) (reference : Reference) : string[] =
        let ver = target.FrameworkVersion.ToString()

        if Path.IsPathRooted reference.Include then


            [| yield reference.Include; if dependsOnFacade reference.Include then yield! getFacade ver  |]
        elif File.Exists reference.Include then
            let p = Path.GetFullPath reference.Include
            [| yield p; if dependsOnFacade p then yield! getFacade ver |]
        else
            [| sysLib ver reference.Include |]

    let getCompilerParams target fsharpCore (refs : Reference[]) =
        let references =
            refs
            |> Array.map (getPathToReference target)
            |> Array.collect id
            |> Array.filter (fun r -> r.Contains "FSharp.Core" |> not && r.Contains "mscorlib" |> not)
            |> Array.distinct
        [|
            yield "-r:" + (sysLib  (target.FrameworkVersion.ToString()) "mscorlib")
            yield "-r:" + (fsCore fsharpCore)
            for r in references do
                yield "-r:" + r
        |]

module ProjectReferences =
    open System.IO

    let getTomlReference (target : Target.Target) (reference : ProjectReference) =
        let path = reference.Include |> Path.GetFullPath
        let proj = FsToml.Parser.parse path
        let config = proj.Configurations |> Target.getConfig target
        let name = getName proj
        Configuration.getOutputPath config name

    let getFsprojReference (target : Target.Target) (reference : ProjectReference) =
        let path = reference.Include |> Path.GetFullPath
        ""

    let getCompilerParams target (references : ProjectReference[]) =
        references |> Array.map (fun r -> "-r:" + if r.Include.EndsWith ".fstoml" then getTomlReference target r else getFsprojReference target r)




module Files =
    let getCompilerParams (files : SourceFile[]) =
        files
        |> Array.filter(fun r -> r.OnBuild = BuildAction.Compile)
        |> Array.map(fun r -> r.Link |> Option.fold (fun s e -> e) r.Include)





let getCompilerParams (target : Target.Target) (project : FsTomlProject) =

    let name = getName project
    let cfg = project.Configurations |> Target.getConfig target |> Configuration.getCompilerParams target name
    let refs = project.References |> References.getCompilerParams target (project.FSharpCore.ToString())
    let files = project.Files |> Files.getCompilerParams
    let projRefs = project.ProjectReferences |> ProjectReferences.getCompilerParams target

    [|
        yield "--noframework"
        yield "--fullpaths"
        yield "--flaterrors"
        yield "--subsystemversion:6.00"
        yield "--highentropyva+"
        yield "--target:" + project.OutputType.ToString()
        yield! cfg
        yield! refs
        yield! projRefs
        yield! files

    |]


let getFSharpProjectOptions  (target : Target.Target) (project : FsTomlProject) =
    let parms = getCompilerParams target project
    let checker = FSharpChecker.Instance
    checker.GetProjectOptionsFromCommandLineArgs (project.Name, parms)




