﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Threading
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Linq
open System.Text
open System.Runtime.InteropServices
open System.Reflection.PortableExecutable

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.Navigation
open Microsoft.VisualStudio.ComponentModelHost

open Microsoft.VisualStudio
open Microsoft.VisualStudio.Editor
open Microsoft.VisualStudio.Threading
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.Shell.Interop
open Microsoft.VisualStudio.TextManager.Interop

open FSharp.Compiler
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text


module private Symbol =
    let fullName (root: ISymbol) : string =
        let rec inner parts (sym: ISymbol) =
            match sym with
            | null ->
                parts
            // TODO: do we have any other terminating cases?
            | sym when sym.Kind = SymbolKind.NetModule || sym.Kind = SymbolKind.Assembly ->
                parts
            | sym when sym.MetadataName <> "" ->
                inner (sym.MetadataName :: parts) sym.ContainingSymbol
            | sym ->
                inner parts sym.ContainingSymbol

        inner [] root |> String.concat "."

module private FindDeclExternalType =
    let rec tryOfRoslynType (typesym: ITypeSymbol): FindDeclExternalType option =
        match typesym with
        | :? IPointerTypeSymbol as ptrparam ->
            tryOfRoslynType ptrparam.PointedAtType |> Option.map FindDeclExternalType.Pointer
        | :? IArrayTypeSymbol as arrparam ->
            tryOfRoslynType arrparam.ElementType |> Option.map FindDeclExternalType.Array
        | :? ITypeParameterSymbol as typaram ->
            Some (FindDeclExternalType.TypeVar typaram.Name)
        | :? INamedTypeSymbol as namedTypeSym ->
            namedTypeSym.TypeArguments
            |> Seq.map tryOfRoslynType
            |> List.ofSeq
            |> Option.ofOptionList
            |> Option.map (fun genericArgs ->
                FindDeclExternalType.Type (Symbol.fullName typesym, genericArgs))
        | _ ->
            Debug.Assert(false, sprintf "GoToDefinitionService: Unexpected Roslyn type symbol subclass: %O" (typesym.GetType()))
            None

module private FindDeclExternalParam =

    let tryOfRoslynParameter (param: IParameterSymbol): FindDeclExternalParam option =
        FindDeclExternalType.tryOfRoslynType param.Type
        |> Option.map (fun ty -> FindDeclExternalParam.Create(ty, param.RefKind <> RefKind.None))

    let tryOfRoslynParameters (paramSyms: ImmutableArray<IParameterSymbol>): FindDeclExternalParam list option =
        paramSyms
        |> Seq.map tryOfRoslynParameter
        |> Seq.toList
        |> Option.ofOptionList

module private ExternalSymbol =
    let rec ofRoslynSymbol (symbol: ISymbol) : (ISymbol * FindDeclExternalSymbol) list =
        let container = Symbol.fullName symbol.ContainingSymbol

        match symbol with
        | :? INamedTypeSymbol as typesym ->
            let fullTypeName = Symbol.fullName typesym

            let constructors =
                typesym.InstanceConstructors
                |> Seq.choose<_,ISymbol * FindDeclExternalSymbol> (fun methsym ->
                    FindDeclExternalParam.tryOfRoslynParameters methsym.Parameters
                    |> Option.map (fun args -> upcast methsym, FindDeclExternalSymbol.Constructor(fullTypeName, args))
                    )
                |> List.ofSeq
                
            (symbol, FindDeclExternalSymbol.Type fullTypeName) :: constructors

        | :? IMethodSymbol as methsym ->
            FindDeclExternalParam.tryOfRoslynParameters methsym.Parameters
            |> Option.map (fun args ->
                symbol, FindDeclExternalSymbol.Method(container, methsym.MetadataName, args, methsym.TypeParameters.Length))
            |> Option.toList

        | :? IPropertySymbol as propsym ->
            [upcast propsym, FindDeclExternalSymbol.Property(container, propsym.MetadataName)]

        | :? IFieldSymbol as fieldsym ->
            [upcast fieldsym, FindDeclExternalSymbol.Field(container, fieldsym.MetadataName)]

        | :? IEventSymbol as eventsym ->
            [upcast eventsym, FindDeclExternalSymbol.Event(container, eventsym.MetadataName)]

        | _ -> []

// TODO: Uncomment code when VS has a fix for updating the status bar.
type internal StatusBar(statusBar: IVsStatusbar) =
    let mutable _searchIcon = int16 Microsoft.VisualStudio.Shell.Interop.Constants.SBAI_Find :> obj

    let _clear() =
        // unfreeze the statusbar
        statusBar.FreezeOutput 0 |> ignore  
        statusBar.Clear() |> ignore
        
    member _.Message(_msg: string) =
        ()
        //let _, frozen = statusBar.IsFrozen()
        //// unfreeze the status bar
        //if frozen <> 0 then statusBar.FreezeOutput 0 |> ignore
        //statusBar.SetText msg |> ignore
        //// freeze the status bar
        //statusBar.FreezeOutput 1 |> ignore

    member this.TempMessage(_msg: string) =
        ()
        //this.Message msg
        //async {
        //    do! Async.Sleep 4000
        //    match statusBar.GetText() with
        //    | 0, currentText when currentText <> msg -> ()
        //    | _ -> clear()
        //}|> Async.Start
    
    member _.Clear() = () //clear()

    /// Animated magnifying glass that displays on the status bar while a symbol search is in progress.
    member _.Animate() : IDisposable = 
        //statusBar.Animation (1, &searchIcon) |> ignore
        { new IDisposable with
            member _.Dispose() = () } //statusBar.Animation(0, &searchIcon) |> ignore }

type internal FSharpGoToDefinitionNavigableItem(document, sourceSpan) =
    inherit FSharpNavigableItem(Glyph.BasicFile, ImmutableArray.Empty, document, sourceSpan)

[<RequireQualifiedAccess>]
type internal FSharpGoToDefinitionResult =
    | NavigableItem of FSharpNavigableItem
    | ExternalAssembly of ProjectInfo * DocumentInfo * FSharpSymbolUse * FindDeclExternalSymbol

type internal GoToDefinition(checker: FSharpChecker, projectInfoManager: FSharpProjectOptionsManager) =
    let userOpName = "GoToDefinition"

    /// Use an origin document to provide the solution & workspace used to 
    /// find the corresponding textSpan and INavigableItem for the range
    let rangeToNavigableItem (range: range, document: Document) = 
        async {
            let fileName = try System.IO.Path.GetFullPath range.FileName with _ -> range.FileName
            let refDocumentIds = document.Project.Solution.GetDocumentIdsWithFilePath fileName
            if not refDocumentIds.IsEmpty then 
                let refDocumentId = refDocumentIds.First()
                let refDocument = document.Project.Solution.GetDocument refDocumentId
                let! cancellationToken = Async.CancellationToken
                let! refSourceText = refDocument.GetTextAsync(cancellationToken) |> Async.AwaitTask
                match RoslynHelpers.TryFSharpRangeToTextSpan (refSourceText, range) with 
                | None -> return None
                | Some refTextSpan -> return Some (FSharpGoToDefinitionNavigableItem (refDocument, refTextSpan))
            else return None
        }

    /// Helper function that is used to determine the navigation strategy to apply, can be tuned towards signatures or implementation files.
    member private _.FindSymbolHelper (originDocument: Document, originRange: range, sourceText: SourceText, preferSignature: bool) =
        asyncMaybe {
            let! parsingOptions, projectOptions = projectInfoManager.TryGetOptionsForEditingDocumentOrProject(originDocument, CancellationToken.None, userOpName)
            let defines = CompilerEnvironment.GetCompilationDefinesForEditing parsingOptions
            let! originTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan (sourceText, originRange)
            let position = originTextSpan.Start
            let! lexerSymbol = Tokenizer.getSymbolAtPosition (originDocument.Id, sourceText, position, originDocument.FilePath, defines, SymbolLookupKind.Greedy, false, false)
            
            let textLinePos = sourceText.Lines.GetLinePosition position
            let fcsTextLineNumber = Line.fromZ textLinePos.Line
            let lineText = (sourceText.Lines.GetLineFromPosition position).ToString()  
            
            let! _, _, checkFileResults = checker.ParseAndCheckDocument (originDocument, projectOptions, userOpName=userOpName)
            let idRange = lexerSymbol.Ident.idRange
            let! fsSymbolUse = checkFileResults.GetSymbolUseAtLocation (fcsTextLineNumber, idRange.EndColumn, lineText, lexerSymbol.FullIsland)
            let symbol = fsSymbolUse.Symbol
            // if the tooltip was spawned in an implementation file and we have a range targeting
            // a signature file, try to find the corresponding implementation file and target the
            // desired symbol
            if isSignatureFile fsSymbolUse.FileName && preferSignature = false then 
                let fsfilePath = Path.ChangeExtension (originRange.FileName,"fs")
                if not (File.Exists fsfilePath) then return! None else
                let! implDoc = originDocument.Project.Solution.TryGetDocumentFromPath fsfilePath
                let! implSourceText = implDoc.GetTextAsync ()
                let! _parsingOptions, projectOptions = projectInfoManager.TryGetOptionsForEditingDocumentOrProject(implDoc, CancellationToken.None, userOpName)
                let! _, _, checkFileResults = checker.ParseAndCheckDocument (implDoc, projectOptions, userOpName=userOpName)
                let symbolUses = checkFileResults.GetUsesOfSymbolInFile symbol
                let! implSymbol  = symbolUses |> Array.tryHead 
                let! implTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan (implSourceText, implSymbol.Range)
                return FSharpGoToDefinitionNavigableItem (implDoc, implTextSpan)
            else
                let! targetDocument = originDocument.Project.Solution.TryGetDocumentFromFSharpRange fsSymbolUse.Range
                return! rangeToNavigableItem (fsSymbolUse.Range, targetDocument)
        }

    /// if the symbol is defined in the given file, return its declaration location, otherwise use the targetSymbol to find the first 
    /// instance of its presence in the provided source file. The first case is needed to return proper declaration location for
    /// recursive type definitions, where the first its usage may not be the declaration.
    member _.FindSymbolDeclarationInDocument(targetSymbolUse: FSharpSymbolUse, document: Document, options: FSharpProjectOptions) = 
        asyncMaybe {
            let filePath = document.FilePath
            match targetSymbolUse.Symbol.DeclarationLocation with
            | Some decl when decl.FileName = filePath -> return decl
            | _ ->
                let! _, _, checkFileResults = checker.ParseAndCheckDocument(document, options, userOpName)
                let symbolUses = checkFileResults.GetUsesOfSymbolInFile targetSymbolUse.Symbol
                let! implSymbol  = symbolUses |> Array.tryHead 
                return implSymbol.Range
        }

    member private this.FindDefinitionAtPosition(originDocument: Document, position: int) =
        asyncMaybe {
            let! parsingOptions, projectOptions = projectInfoManager.TryGetOptionsForEditingDocumentOrProject(originDocument, CancellationToken.None, userOpName)
            let! sourceText = originDocument.GetTextAsync () |> liftTaskAsync
            let defines = CompilerEnvironment.GetCompilationDefinesForEditing parsingOptions
            let textLine = sourceText.Lines.GetLineFromPosition position
            let textLinePos = sourceText.Lines.GetLinePosition position
            let textLineString = textLine.ToString()
            let fcsTextLineNumber = Line.fromZ textLinePos.Line
            let lineText = (sourceText.Lines.GetLineFromPosition position).ToString()  
            
            let preferSignature = isSignatureFile originDocument.FilePath

            let! _, _, checkFileResults = checker.ParseAndCheckDocument (originDocument, projectOptions, userOpName=userOpName)
                
            let! lexerSymbol = Tokenizer.getSymbolAtPosition (originDocument.Id, sourceText, position,originDocument.FilePath, defines, SymbolLookupKind.Greedy, false, false)
            let idRange = lexerSymbol.Ident.idRange

            let declarations = checkFileResults.GetDeclarationLocation (fcsTextLineNumber, lexerSymbol.Ident.idRange.EndColumn, textLineString, lexerSymbol.FullIsland, preferSignature)
            let! targetSymbolUse = checkFileResults.GetSymbolUseAtLocation (fcsTextLineNumber, idRange.EndColumn, lineText, lexerSymbol.FullIsland)

            match declarations with
            | FindDeclResult.ExternalDecl (assembly, targetExternalSym) ->
                let projectOpt = originDocument.Project.Solution.Projects |> Seq.tryFind (fun p -> p.AssemblyName.Equals(assembly, StringComparison.OrdinalIgnoreCase))
                match projectOpt with
                | Some project ->
                    let! symbols = SymbolFinder.FindSourceDeclarationsAsync(project, fun _ -> true)

                    let roslynSymbols =
                        symbols
                        |> Seq.collect ExternalSymbol.ofRoslynSymbol
                        |> Array.ofSeq

                    let! symbol =
                        roslynSymbols
                        |> Seq.tryPick (fun (sym, externalSym) ->
                            if externalSym = targetExternalSym then Some sym
                            else None
                            )

                    let! location = symbol.Locations |> Seq.tryHead
                    return (FSharpGoToDefinitionResult.NavigableItem(FSharpGoToDefinitionNavigableItem(project.GetDocument(location.SourceTree), location.SourceSpan)), idRange)
                | _ ->
                    let tmpProjInfo, tmpDocId = MetadataAsSource.generateTemporaryCSharpDocument(AssemblyIdentity(targetSymbolUse.Symbol.Assembly.QualifiedName), targetSymbolUse.Symbol.DisplayName, originDocument.Project.MetadataReferences)
                    return (FSharpGoToDefinitionResult.ExternalAssembly(tmpProjInfo, tmpDocId, targetSymbolUse, targetExternalSym), idRange)

            | FindDeclResult.DeclFound targetRange -> 
                // if goto definition is called at we are alread at the declaration location of a symbol in
                // either a signature or an implementation file then we jump to it's respective postion in thethe
                if lexerSymbol.Range = targetRange then
                    // jump from signature to the corresponding implementation
                    if isSignatureFile originDocument.FilePath then
                        let implFilePath = Path.ChangeExtension (originDocument.FilePath,"fs")
                        if not (File.Exists implFilePath) then return! None else
                        let! implDocument = originDocument.Project.Solution.TryGetDocumentFromPath implFilePath
                        
                        let! targetRange = this.FindSymbolDeclarationInDocument(targetSymbolUse, implDocument, projectOptions)
                        let! implSourceText = implDocument.GetTextAsync() |> liftTaskAsync
                        let! implTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan (implSourceText, targetRange)
                        let navItem = FSharpGoToDefinitionNavigableItem (implDocument, implTextSpan)
                        return (FSharpGoToDefinitionResult.NavigableItem(navItem), idRange)
                    else // jump from implementation to the corresponding signature
                        let declarations = checkFileResults.GetDeclarationLocation (fcsTextLineNumber, lexerSymbol.Ident.idRange.EndColumn, textLineString, lexerSymbol.FullIsland, true)
                        match declarations with
                        | FindDeclResult.DeclFound targetRange -> 
                            let! sigDocument = originDocument.Project.Solution.TryGetDocumentFromPath targetRange.FileName
                            let! sigSourceText = sigDocument.GetTextAsync () |> liftTaskAsync
                            let! sigTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan (sigSourceText, targetRange)
                            let navItem = FSharpGoToDefinitionNavigableItem (sigDocument, sigTextSpan)
                            return (FSharpGoToDefinitionResult.NavigableItem(navItem), idRange)
                        | _ ->
                            return! None
                // when the target range is different follow the navigation convention of 
                // - gotoDefn origin = signature , gotoDefn destination = signature
                // - gotoDefn origin = implementation, gotoDefn destination = implementation 
                else
                    let! sigDocument = originDocument.Project.Solution.TryGetDocumentFromPath targetRange.FileName
                    let! sigSourceText = sigDocument.GetTextAsync () |> liftTaskAsync
                    let! sigTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan (sigSourceText, targetRange)
                    // if the gotodef call originated from a signature and the returned target is a signature, navigate there
                    if isSignatureFile targetRange.FileName && preferSignature then 
                        let navItem = FSharpGoToDefinitionNavigableItem (sigDocument, sigTextSpan)
                        return (FSharpGoToDefinitionResult.NavigableItem(navItem), idRange)
                    else // we need to get an FSharpSymbol from the targetRange found in the signature
                         // that symbol will be used to find the destination in the corresponding implementation file
                        let implFilePath =
                            // Bugfix: apparently sigDocument not always is a signature file
                            if isSignatureFile sigDocument.FilePath then Path.ChangeExtension (sigDocument.FilePath, "fs") 
                            else sigDocument.FilePath

                        let! implDocument = originDocument.Project.Solution.TryGetDocumentFromPath implFilePath

                        let! _parsingOptions, projectOptions = projectInfoManager.TryGetOptionsForEditingDocumentOrProject(implDocument, CancellationToken.None, userOpName)
                        
                        let! targetRange = this.FindSymbolDeclarationInDocument(targetSymbolUse, implDocument, projectOptions)    
                        
                        let! implSourceText = implDocument.GetTextAsync () |> liftTaskAsync
                        let! implTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan (implSourceText, targetRange)
                        let navItem = FSharpGoToDefinitionNavigableItem (implDocument, implTextSpan)
                        return (FSharpGoToDefinitionResult.NavigableItem(navItem), idRange)
                | _ ->
                    return! None
        }

    /// find the declaration location (signature file/.fsi) of the target symbol if possible, fall back to definition 
    member this.FindDeclarationOfSymbolAtRange(targetDocument: Document, symbolRange: range, targetSource: SourceText) =
        this.FindSymbolHelper(targetDocument, symbolRange, targetSource, preferSignature=true)

    /// find the definition location (implementation file/.fs) of the target symbol
    member this.FindDefinitionOfSymbolAtRange(targetDocument: Document, symbolRange: range, targetSourceText: SourceText) =
        this.FindSymbolHelper(targetDocument, symbolRange, targetSourceText, preferSignature=false)

    member this.FindDefinitionsForPeekTask(originDocument: Document, position: int, cancellationToken: CancellationToken) =
        this.FindDefinitionAtPosition(originDocument, position)
        |> Async.map (
                Option.toArray
                >> Array.toSeq)
        |> RoslynHelpers.StartAsyncAsTask cancellationToken

    /// Construct a task that will return a navigation target for the implementation definition of the symbol 
    /// at the provided position in the document.
    member this.FindDefinitionTask(originDocument: Document, position: int, cancellationToken: CancellationToken) =
        this.FindDefinitionAtPosition(originDocument, position)
        |> RoslynHelpers.StartAsyncAsTask cancellationToken

    /// Navigate to the positon of the textSpan in the provided document
    /// used by quickinfo link navigation when the tooltip contains the correct destination range.
    member _.TryNavigateToTextSpan(document: Document, textSpan: Microsoft.CodeAnalysis.Text.TextSpan, statusBar: StatusBar) =
        let navigableItem = FSharpGoToDefinitionNavigableItem(document, textSpan)
        let workspace = document.Project.Solution.Workspace
        let navigationService = workspace.Services.GetService<IFSharpDocumentNavigationService>()
        let options = workspace.Options.WithChangedOption(FSharpNavigationOptions.PreferProvisionalTab, true)
        let navigationSucceeded = navigationService.TryNavigateToSpan(workspace, navigableItem.Document.Id, navigableItem.SourceSpan, options)

        if not navigationSucceeded then 
            statusBar.TempMessage (SR.CannotNavigateUnknown())

    member _.NavigateToItem(navigableItem: FSharpNavigableItem, statusBar: StatusBar) =
        use __ = statusBar.Animate()

        statusBar.Message (SR.NavigatingTo())

        let workspace = navigableItem.Document.Project.Solution.Workspace
        let navigationService = workspace.Services.GetService<IFSharpDocumentNavigationService>()

        // Prefer open documents in the preview tab.
        let options = workspace.Options.WithChangedOption(FSharpNavigationOptions.PreferProvisionalTab, true)
        let result = navigationService.TryNavigateToSpan(workspace, navigableItem.Document.Id, navigableItem.SourceSpan, options)
            
        if result then 
            statusBar.Clear()
        else 
            statusBar.TempMessage (SR.CannotNavigateUnknown())

    /// Find the declaration location (signature file/.fsi) of the target symbol if possible, fall back to definition 
    member this.NavigateToSymbolDeclarationAsync(targetDocument: Document, targetSourceText: SourceText, symbolRange: range, statusBar: StatusBar) =
        asyncMaybe {
            let! item = this.FindDeclarationOfSymbolAtRange(targetDocument, symbolRange, targetSourceText)
            return this.NavigateToItem(item, statusBar)
        }

    /// Find the definition location (implementation file/.fs) of the target symbol
    member this.NavigateToSymbolDefinitionAsync(targetDocument: Document, targetSourceText: SourceText, symbolRange: range, statusBar: StatusBar) =
        asyncMaybe {
            let! item = this.FindDefinitionOfSymbolAtRange(targetDocument, symbolRange, targetSourceText)
            return this.NavigateToItem(item, statusBar)
        }
