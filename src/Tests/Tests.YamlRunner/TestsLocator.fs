module Tests.YamlRunner.TestsLocator

open System
open System.Globalization
open System.IO
open System.Threading
open FSharp.Data
open Tests.YamlRunner.AsyncExtensions
open ShellProgressBar
open Tests.YamlRunner

let ListFolders namedSuite revision directory = async {
    let url = TestsDownloader.TestGithubRootUrl namedSuite revision
    let! (_, html) = TestsDownloader.CachedOrDownload revision "_root_" "index.html" url 
    let doc = HtmlDocument.Parse(html)
    
    return
        doc.CssSelect("td.content a.js-navigation-open")
        |> List.map (fun a -> a.InnerText())
        |> List.filter (fun f -> not <| f.StartsWith("cluster"))
        |> List.filter (fun f -> not <| f.StartsWith("cat"))
        |> List.filter (fun f -> match directory with | Some s -> f = s | None -> true)
        |> List.filter (fun f -> not <| f.EndsWith(".asciidoc"))
}
    
let ListFolderFiles namedSuite revision folder fileFilter = async { 
    let url = TestsDownloader.FolderListUrl namedSuite revision folder
    let! (_, html) =  TestsDownloader.CachedOrDownload revision folder "index.html" url 
    let doc = HtmlDocument.Parse(html)
    let yamlFiles =
        let fileUrl file = (file, TestsDownloader.TestRawUrl namedSuite revision folder file)
        doc.CssSelect("td.content a.js-navigation-open")
        |> List.map(fun a -> a.InnerText())
        |> List.filter(fun f -> f.EndsWith(".yml"))
        |> List.filter (fun f -> match fileFilter with | Some s -> f.StartsWith(s, StringComparison.OrdinalIgnoreCase) | None -> true)
        //|> List.filter(fun f -> f = "51_refresh_with_types.yml")
        |> List.map fileUrl
    return yamlFiles
}

type YamlFileInfo = { File: string; Yaml: string }

let TestLocalFile file =
    let yaml = System.IO.File.ReadAllText file
    { File = file; Yaml = yaml }

let private downloadTestsInFolder (yamlFiles:list<string * string>) folder revision (progress: IProgressBar) subBarOptions = async {
    let mutable seenFiles = 0;
    use filesProgress = progress.Spawn(yamlFiles.Length, sprintf "Downloading [0/%i] files in %s" yamlFiles.Length folder, subBarOptions)
    let actions =
        yamlFiles
        |> Seq.map (fun (file, url) -> async {
            let! (localFile, yaml) =  TestsDownloader.CachedOrDownload revision folder file url
            let i = Interlocked.Increment (&seenFiles)
            let message = sprintf "Downloaded [%i/%i] files in %s" i yamlFiles.Length folder
            filesProgress.Tick(message)
            match String.IsNullOrWhiteSpace yaml with
            | true ->
                progress.WriteLine(sprintf "Skipped %s since it returned no data" url)
                return None
            | _ ->
                return Some {File = localFile; Yaml = yaml}
        })
        |> Seq.toList
        
    let! completed = Async.ForEachAsync 4 actions
    let files = completed |> List.choose id;
    return files 
}

type LocateResults = { Folder: string; Paths: YamlFileInfo list } 

let DownloadTestsInFolder folder fileFilter namedSuite revision (progress: IProgressBar) subBarOptions = async {
    let! token = Async.StartChild <| ListFolderFiles namedSuite revision folder fileFilter
    let! yamlFiles = token
    let! localFiles = async {
       match yamlFiles.Length with
       | 0 ->
           //progress.WriteLine(sprintf "%s folder yielded no tests" folder)
           return List.empty
       | x ->
           let! result = downloadTestsInFolder yamlFiles folder revision progress subBarOptions
           return result
    }
    progress.Tick()
    return { Folder = folder; Paths = localFiles }
}
