﻿#light
namespace Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Tagging
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Classification
open Microsoft.VisualStudio.Utilities
open System.ComponentModel.Composition
open System.Collections.Generic

/// This is the type responsible for tracking a line + column across edits to the
/// underlying ITextBuffer.  In a perfect world this would be implemented as an 
/// ITrackingSpan so we didn't have to update the locations on every single 
/// change to the ITextBuffer.  
///
/// Unfortunately the limitations of the ITrackingSpaninterface prevent us from doing 
/// that. One strategy you might employ is to say you'll track the Span which represents
/// the extent of the line you care about.  This works great right until you consider
/// the case where the line break before your Span is deleted.  Because you can't access 
/// the full text of that ITextVersion you can't "find" the new front of the line
/// you are tracking (same happens with the end).
///
/// So for now we're stuck with updating these on every edit to the ITextBuffer.  Not
/// ideal but will have to do for now.  
type internal TrackingLineColumn 
    ( 
        _textBuffer : ITextBuffer,
        _column : int,
        _mode : LineColumnTrackingMode,
        _onClose : TrackingLineColumn -> unit ) =

    /// This is the SnapshotSpan of the line that we are tracking.  It is None in the
    /// case of a deleted line
    let mutable _line : ITextSnapshotLine option  = None

    /// When the line this TrackingLineColumn is deleted, this will record the version 
    /// number of the last valid version containing the line.  That way if we undo this 
    /// can become valid again
    let mutable _lastValidVersion : (int * int) option  = None

    member x.TextBuffer = _textBuffer

    member x.Line 
        with get() = _line
        and set value = _line <- value

    member x.Column = _column

    member x.SurviveDeletes = 
        match _mode with
        | LineColumnTrackingMode.Default -> false
        | LineColumnTrackingMode.SurviveDeletes -> true

    member private x.VirtualSnapshotPoint = 
        match _line with
        | None -> None
        | Some line -> Some (VirtualSnapshotPoint(line, _column))

    /// Update the internal tracking information based on the new ITextSnapshot
    member x.UpdateForChange (e : TextContentChangedEventArgs) =
        let newSnapshot = e.After
        let changes = e.Changes

        // We have a valid line.  Need to update against this set of changes
        let withValidLine (oldLine : ITextSnapshotLine) =

            // For whatever reason this is now invalid.  Store the last good information so we can
            // recover during an undo operation
            let makeInvalid () = 
                _line <- None
                _lastValidVersion <- Some (oldLine.Snapshot.Version.VersionNumber, oldLine.LineNumber)

            // Is this change a delete of the entire line 
            let isLineDelete (change : ITextChange) = 
                change.LineCountDelta < 0 &&
                change.OldSpan.Contains(oldLine.ExtentIncludingLineBreak.Span)

            if (not x.SurviveDeletes) && Seq.exists isLineDelete e.Changes then
                // If this shouldn't survive a full line deletion and there is a deletion
                // then we are invalid
                makeInvalid()
            else

                // Calculate the line number delta for this given change. All we care about here
                // is the line number.  So changes line shortening the line don't matter for 
                // us.
                let getLineNumberDelta (change : ITextChange) =
                    if change.LineCountDelta = 0 || change.OldPosition > oldLine.Start.Position then
                        // If there is no line change or this change occurred after the start 
                        // of our line then there is nothing to process
                        0
                    else 
                        // The change occurred before our line and there is a delta.  This is the 
                        // delta we need to apply to our line number
                        change.LineCountDelta

                // Calculate the line delta
                let delta = 
                    e.Changes
                    |> Seq.map getLineNumberDelta
                    |> Seq.sum
                let number = oldLine.LineNumber + delta
                match SnapshotUtil.TryGetLine newSnapshot number with
                | None -> makeInvalid()
                | Some line -> _line <- Some line

        // This line was deleted at some point in the past and hence we're invalid.  If the 
        // current change is an Undo back to the last version where we were valid then we
        // become valid again
        let checkUndo lastVersion lastLineNumber = 
            let newVersion = e.AfterVersion
            if newVersion.ReiteratedVersionNumber = lastVersion && lastLineNumber <= newSnapshot.LineCount then 
                _line <- Some (newSnapshot.GetLineFromLineNumber(lastLineNumber))
                _lastValidVersion <- None

        match _line, _lastValidVersion with
        | Some line, _ -> withValidLine line
        | None, Some (version,lineNumber) -> checkUndo version lineNumber
        | _ -> ()

    override x.ToString() =
        match x.VirtualSnapshotPoint with
        | Some(point) ->
            let line,_ = SnapshotPointUtil.GetLineColumn point.Position
            sprintf "%d,%d - %s" line _column (point.ToString())
        | None -> "Invalid"

    interface ITrackingLineColumn with
        member x.TextBuffer = _textBuffer
        member x.TrackingMode = _mode
        member x.VirtualPoint = x.VirtualSnapshotPoint
        member x.Point = 
            match x.VirtualSnapshotPoint with
            | None -> None
            | Some point -> Some point.Position
        member x.Close () =
            _onClose x
            _line <- None
            _lastValidVersion <- None

/// Implementation of ITrackingVisualSpan which is used to track a VisualSpan across edits
/// to the ITextBuffer
[<RequireQualifiedAccess>]
type internal TrackingVisualSpan =

    /// Tracks the origin SnapshotPoint of the character span, the number of lines it 
    /// composes of and the length of the span on the last line
    | Character of ITrackingLineColumn * int * int

    /// Tracks the origin of the SnapshotLineRange and the number of lines it should comprise
    /// of
    | Line of ITrackingLineColumn * int

    /// Tracks the origin of the block selection, it's width by height
    | Block of ITrackingLineColumn * int * int

    with

    member x.TrackingLineColumn =
        match x with
        | Character (trackingLineColumn, _, _) -> trackingLineColumn
        | Line (trackingLineColumn, _) -> trackingLineColumn
        | Block (trackingLineColumn, _, _) -> trackingLineColumn

    member x.TextBuffer =
        x.TrackingLineColumn.TextBuffer

    /// Calculate the VisualSpan against the current ITextSnapshot
    member x.VisualSpan = 
        let snapshot = x.TextBuffer.CurrentSnapshot

        match x.TrackingLineColumn.Point with
        | None ->
            None
        | Some point ->
            match x with
            | Character (_, lineCount, lastLineLength) ->
                let characterSpan = CharacterSpan(point, lineCount, lastLineLength)
                VisualSpan.Character characterSpan
            | Line (_, count) ->
                let line = SnapshotPointUtil.GetContainingLine point
                SnapshotLineRangeUtil.CreateForLineAndMaxCount line count 
                |> VisualSpan.Line
            | Block (_, width, height) ->
                let blockSpan = BlockSpan(point, width, height)
                VisualSpan.Block blockSpan
            |> Some

    member x.Close() =
        match x with
        | Character _ -> ()
        | Line (trackingLineColumn, _) -> trackingLineColumn.Close()
        | Block (trackingLineColumn, _, _) -> trackingLineColumn.Close()

    static member Create (bufferTrackingService : IBufferTrackingService) visualSpan =
        match visualSpan with
        | VisualSpan.Character characterSpan ->

            // Implemented by tracking the start point of the SnapshotSpan, the number of lines
            // in the span and the length of the final line
            let textBuffer = characterSpan.Snapshot.TextBuffer
            let trackingLineColumn = 
                let line, column = SnapshotPointUtil.GetLineColumn characterSpan.Start
                bufferTrackingService.CreateLineColumn textBuffer line column LineColumnTrackingMode.Default

            TrackingVisualSpan.Character (trackingLineColumn, characterSpan.LineCount, characterSpan.LastLineLength)

        | VisualSpan.Line snapshotLineRange ->

            // Setup an ITrackingLineColumn at the 0 column of the first line.  This actually may be doable
            // with an ITrackingPoint but for now sticking with an ITrackinglineColumn
            let textBuffer = snapshotLineRange.Snapshot.TextBuffer
            let trackingLineColumn = bufferTrackingService.CreateLineColumn textBuffer snapshotLineRange.StartLineNumber 0 LineColumnTrackingMode.Default
            TrackingVisualSpan.Line (trackingLineColumn, snapshotLineRange.Count)

        | VisualSpan.Block blockSpan ->

            // Setup an ITrackLineColumn at the top left of the block selection
            let trackingLineColumn =
                let textBuffer = blockSpan.TextBuffer
                let lineNumber, column = SnapshotPointUtil.GetLineColumn blockSpan.Start

                bufferTrackingService.CreateLineColumn textBuffer lineNumber column LineColumnTrackingMode.Default

            TrackingVisualSpan.Block (trackingLineColumn, blockSpan.Width, blockSpan.Height)

    interface ITrackingVisualSpan with
        member x.TextBuffer = x.TextBuffer
        member x.VisualSpan = x.VisualSpan
        member x.Close() = x.Close()

type internal TrackingVisualSelection 
    (
        _trackingVisualSpan : ITrackingVisualSpan,
        _path : Path,
        _column : int,
        _blockCaretLocation : BlockCaretLocation
    ) =

    member x.VisualSelection =
        match _trackingVisualSpan.VisualSpan with
        | None -> 
            None
        | Some visualSpan -> 
            let visualSelection =
                match visualSpan with
                | VisualSpan.Character span -> VisualSelection.Character (span, _path)
                | VisualSpan.Line lineRange -> VisualSelection.Line (lineRange, _path, _column)
                | VisualSpan.Block blockSpan -> VisualSelection.Block (blockSpan, _blockCaretLocation)
            Some visualSelection

    member x.CaretPoint =
        x.VisualSelection |> Option.map (fun visualSelection -> visualSelection.CaretPoint)

    /// Get the caret in the given VisualSpan
    member x.GetCaret visualSpan = 
        x.VisualSelection |> Option.map (fun visualSelection -> visualSelection.CaretPoint)

    /// Create an ITrackingVisualSelection with the provided data
    static member Create (bufferTrackingService : IBufferTrackingService) (visualSelection : VisualSelection)=

        // Track the inclusive VisualSpan.  Internally the VisualSelection type represents values as inclusive
        // ones.  
        let trackingVisualSpan = bufferTrackingService.CreateVisualSpan (visualSelection.GetVisualSpan SelectionKind.Inclusive)
        let path, column, blockCaretLocation =
            match visualSelection with
            | VisualSelection.Character (_, path) -> path, 0, BlockCaretLocation.TopRight
            | VisualSelection.Line (_, path, column) -> path, column, BlockCaretLocation.TopRight
            | VisualSelection.Block (_, blockCaretLocation) -> Path.Forward, 0, blockCaretLocation
        TrackingVisualSelection(trackingVisualSpan, path, column, blockCaretLocation)

    interface ITrackingVisualSelection with
        member x.CaretPoint = x.CaretPoint
        member x.TextBuffer = _trackingVisualSpan.TextBuffer
        member x.VisualSelection = x.VisualSelection
        member x.Close() = _trackingVisualSpan.Close()

type internal TrackedData = {
    List : WeakReference<TrackingLineColumn> list
    Observer : System.IDisposable 
}

/// Service responsible for tracking various parts of an ITextBuffer which can't be replicated
/// by simply using ITrackingSpan or ITrackingPoint.
[<Export(typeof<IBufferTrackingService>)>]
[<Sealed>]
type internal BufferTrackingService() = 
    
    let _map = new Dictionary<ITextBuffer, TrackedData>()

    /// Gets the data for the passed in buffer.  This method takes care of removing all 
    /// collected WeakReference items and updating the internal map 
    member x.GetData textBuffer foundFunc notFoundFunc =
        let found,data = _map.TryGetValue(textBuffer)
        if not found then notFoundFunc()
        else
            let tlcs = 
                data.List 
                |> Seq.ofList 
                |> Seq.choose (fun weakRef -> weakRef.Target)
            if tlcs |> Seq.isEmpty then
                data.Observer.Dispose()
                _map.Remove(textBuffer) |> ignore
                notFoundFunc()
            else
                foundFunc data.Observer tlcs data.List

    /// Remove the TrackingLineColumn from the map.  If it is the only remaining 
    /// TrackingLineColumn assigned to the ITextBuffer, remove it from the map
    /// and unsubscribe from the Changed event
    member x.Remove (tlc:TrackingLineColumn) = 
        let found (data:System.IDisposable) items rawList = 
            let items = items |> Seq.filter (fun cur -> cur <> tlc)
            if items |> Seq.isEmpty then 
                data.Dispose()
                _map.Remove(tlc.TextBuffer) |> ignore
            else
                let items = [Util.CreateWeakReference tlc] @ rawList
                _map.Item(tlc.TextBuffer) <- { Observer = data; List = items }
        x.GetData tlc.TextBuffer found (fun () -> ())

    /// Add the TrackingLineColumn to the map.  If this is the first item in the
    /// map then subscribe to the Changed event on the buffer
    member x.Add (tlc:TrackingLineColumn) =
        let textBuffer = tlc.TextBuffer
        let found data _ list =
            let list = [Util.CreateWeakReference tlc] @ list
            _map.Item(textBuffer) <- { Observer = data; List = list }
        let notFound () =
            let observer = textBuffer.Changed |> Observable.subscribe x.OnBufferChanged
            let data = { List = [Util.CreateWeakReference tlc]; Observer = observer }
            _map.Add(textBuffer,data)
        x.GetData textBuffer found notFound

    member x.OnBufferChanged (e:TextContentChangedEventArgs) = 
        let found _ (items: TrackingLineColumn seq) _ =
            items |> Seq.iter (fun tlc -> tlc.UpdateForChange e)
        x.GetData e.Before.TextBuffer found (fun () -> ())

    member x.Create (textBuffer:ITextBuffer) lineNumber column mode = 
        let tlc = TrackingLineColumn(textBuffer, column, mode, x.Remove)
        let tss = textBuffer.CurrentSnapshot
        let line = tss.GetLineFromLineNumber(lineNumber)
        tlc.Line <-  Some line
        x.Add tlc
        tlc

    interface IBufferTrackingService with
        member x.CreateLineColumn textBuffer lineNumber column mode = x.Create textBuffer lineNumber column mode :> ITrackingLineColumn
        member x.CreateVisualSpan visualSpan = TrackingVisualSpan.Create x visualSpan :> ITrackingVisualSpan
        member x.CreateVisualSelection visualSelection = TrackingVisualSelection.Create x visualSelection :> ITrackingVisualSelection
        member x.CloseAll() =
            let values = _map.Values |> List.ofSeq
            values 
            |> Seq.ofList
            |> Seq.map (fun data -> data.List)
            |> Seq.concat
            |> Seq.choose (fun item -> item.Target)
            |> Seq.map (fun tlc -> tlc :> ITrackingLineColumn)
            |> Seq.iter (fun tlc -> tlc.Close() )

/// Component which monitors commands across IVimBuffer instances and 
/// updates the LastCommand value for repeat purposes
[<Export(typeof<IVimBufferCreationListener>)>]
type internal ChangeTracker
    [<ImportingConstructor>]
    (
        _vim : IVim
    ) =

    let _vimData = _vim.VimData

    member x.OnVimBufferCreated (buffer : IVimBuffer) =
        let handler = x.OnCommandRan
        buffer.NormalMode.CommandRunner.CommandRan |> Event.add handler
        buffer.VisualLineMode.CommandRunner.CommandRan |> Event.add handler
        buffer.VisualBlockMode.CommandRunner.CommandRan |> Event.add handler
        buffer.VisualCharacterMode.CommandRunner.CommandRan |> Event.add handler
        buffer.InsertMode.CommandRan |> Event.add handler
        buffer.ReplaceMode.CommandRan |> Event.add handler

    member x.OnCommandRan (args : CommandRunDataEventArgs) = 
        let data = args.CommandRunData
        let command = data.CommandBinding
        if command.IsMovement || command.IsSpecial then
            // Movement and special commands don't participate in change tracking
            ()
        elif command.IsRepeatable then
            let storedCommand = StoredCommand.OfCommand data.Command data.CommandBinding
            x.StoreCommand storedCommand
        else 
            _vimData.LastCommand <- None

    /// Store the given StoredCommand as the last command executed in the IVimBuffer.  Take into
    /// account linking with the previous command
    member x.StoreCommand currentCommand = 
        match _vimData.LastCommand with
        | None -> 
            // No last command so no linking to consider
            _vimData.LastCommand <- Some currentCommand
        | Some lastCommand ->
            let shouldLink = 
                Util.IsFlagSet currentCommand.CommandFlags CommandFlags.LinkedWithPreviousCommand ||
                Util.IsFlagSet lastCommand.CommandFlags CommandFlags.LinkedWithNextCommand 
            if shouldLink then
                _vimData.LastCommand <- StoredCommand.LinkedCommand (lastCommand, currentCommand) |> Some
            else
                _vimData.LastCommand <- Some currentCommand

    interface IVimBufferCreationListener with
        member x.VimBufferCreated buffer = x.OnVimBufferCreated buffer


