Imports System.IO
Imports System.Diagnostics
Imports System.Drawing
Imports System.Threading.Tasks

Public Class Form1
    Public Const APP_VERSION As String = "1.25.3"
    ' Folder paths and settings
    Dim GameFolder As String = Path.Combine(Application.StartupPath, "C64", "Games")
    Dim ImageFolder As String = Path.Combine(Application.StartupPath, "C64", "Images")
    Dim ManualFolder As String = Path.Combine(Application.StartupPath, "C64", "Manuals")
    Private _infoFolder As String = Path.Combine(Application.StartupPath, "C64", "Info")

    ' The system on show; its settings live in <system>\Settings
    Private _currentSystem As String = "C64"
    Private ReadOnly _systemNames As New Dictionary(Of String, String) From {
        {"C64", "Commodore 64"}, {"VIC20", "VIC-20"}, {"C16", "C16 / Plus/4"}}

    Private ReadOnly Property _settingsFilePath As String
        Get
            Return Path.Combine(SystemSettingsFolder(_currentSystem), "settings.txt")
        End Get
    End Property
    Private _lastSelectedGame As String = String.Empty
    Private _allGames As New List(Of String)()
    Private _currentSearchTerm As String = ""
    Private _currentImagePath As String = ""
    Private _currentManualPath As String = ""
    Private _editModeEnabled As Boolean = False ' Edit Mode flag
    Private _editModeMenuItem As ToolStripMenuItem = Nothing ' Reference to the Edit Mode menu item

    ' Allowed file extensions
    Private ReadOnly _allowedImageExtensions As String() = {".png", ".jpg", ".jpeg", ".gif", ".bmp"}
    ' Manuals can be in any of these; a .txt one is also shown below the game's info
    Private ReadOnly _allowedManualExtensions As String() = {
        ".txt", ".pdf", ".htm", ".html", ".rtf", ".doc", ".docx", ".odt", ".epub", ".djvu", ".xps",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp"}
    Private Shared ReadOnly _pictureManualExtensions As String() = {".jpg", ".jpeg", ".png", ".gif", ".bmp"}

    ' The Manual button in the corner of the info window, and the game's manuals
    Private _manualButton As Button = Nothing
    Private _currentManuals As New List(Of String)
    Private ReadOnly _allowedGameExtensions As String() = {
        ".tap", ".t64", ".tzx", ".csw", ".prg", ".p00",
        ".wav", ".mp3", ".flac", ".ogg", ".gz"
    }

    ' Maps each name shown in the list to its file. Same-named games (for example
    ' one title on C64 and VIC-20) get a tag so that each stays selectable.
    Private ReadOnly _gameFiles As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    ' Tape output options, kept apart from settings.txt which SettingsForm owns
    Private ReadOnly _optionsFilePath As String = Path.Combine(Application.StartupPath, "tape_options.txt")
    Private _machineOption As String = "auto"
    ' Invert and sine wave for the current system; each system keeps its own, as an
    ' adaptor that needs inverting for one machine can stop another loading
    Private _invertOption As Boolean = False
    Private _sineOption As Boolean = False
    Private ReadOnly _invertBySystem As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _sineBySystem As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    ' Short Leader, for testing: the share of each standard leader tone kept (100, 50 or 25)
    Private _leaderOption As Integer = 100
    Private Shared ReadOnly LEADER_CHOICES As Integer() = {100, 50, 25}
    Private ReadOnly _leaderBySystem As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

    ' Per-system folders: Games\C64, Images\C64\Case, Manuals\C64, Info\C64 and so on
    Private ReadOnly _systemFolders As String() = {"C64", "VIC20", "C16"}
    Private ReadOnly _imageTypeFolders As String() = {"Case", "Title", "Screen"}
    Private ReadOnly _imageTypeNames As String() = {"Game Case", "Title Screen", "Game Screen"}
    Private _imageTypeOption As String = "Case"
    Private _assetProcess As Process = Nothing
    Private _downloadMenuItems As New List(Of ToolStripMenuItem)()

    ' Fast load: games opted out of it (kept in settings.txt) and what each game supports
    Private Const FAST_LOAD_PREFIX As String = "FastLoad="
    Private Const OLD_FAST_LOAD_OFF_PREFIX As String = "FastLoadOff="

    ' Fast load types saved per game: 0 Unavailable, 1 Off, 2 Fast, 3 Turbo, 4 Hyper, 5 Ultra
    Private Const TYPE_UNAVAILABLE As Integer = 0
    Private Const TYPE_OFF As Integer = 1
    Private Const TYPE_ULTRA As Integer = 5
    Private ReadOnly _fastLoadTypeNames As String() = {"Unavailable", "Off", "Fast", "Turbo", "Hyper", "Ultra"}
    Private ReadOnly _fastLoadComboOrder As Integer() = {1, 2, 3, 4, 5, 0}
    Private Const FAST_CHECK_FAILED As String = "failed:"
    Private ReadOnly _fastLoadTypes As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _fastCheckCache As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly Property _fastScanFilePath As String
        Get
            Return Path.Combine(SystemSettingsFolder(_currentSystem), "fastload_scan.txt")
        End Get
    End Property
    Private ReadOnly _tapeTimes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    ' VIC-20 only: the RAM expansion each game needs, such as 8K, 16K or None
    Private ReadOnly _tapeMemory As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _fastCheckFailures As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private _scanRunning As Boolean = False
    Private _scanAgain As Boolean = False
    Private _formClosing As Boolean = False
    Private _fastLoadStatus As String = Nothing
    Private _fastLoadIndicator As Label = Nothing
    Private ReadOnly _fastLoadToolTip As New ToolTip()
    Private _tapeProgress As ProgressBar = Nothing
    Private _tapePercent As Label = Nothing
    Private _fastLoadCaption As Label = Nothing
    Private _fastLoadCombo As ComboBox = Nothing
    Private _updatingFastLoadCombo As Boolean = False

    ' Process and playback control
    Private tapePlayProcess As Process = Nothing
    Private _childProcesses As New List(Of Process)()
    Private controlFilePath As String = Path.Combine(Path.GetTempPath(), "cbm_control.txt")
    Private totalBlocksFilePath As String = Path.Combine(Path.GetTempPath(), "cbm_total_blocks.txt")
    Private nextBlockFilePath As String = Path.Combine(Path.GetTempPath(), "cbm_next_block.txt")

    ' File system watcher
    Private WithEvents _gameFolderWatcher As FileSystemWatcher

    ' Counters and timers
    Private tapeCounter As Integer = 0
    Private totalBlocks As Integer = 0
    Private zeroedBlock As Integer = -1
    Private tapeCounterTimer As New Timer() With {.Interval = 1000}
    Private WithEvents TimerResetStatus As New Timer() With {.Interval = 10000, .Enabled = False}
    Private isStopped As Boolean = False

    ' Scrolling text variables
    Private WithEvents scrollTimer As New Timer() With {.Interval = 100} ' Lower = faster scrolling
    Private scrollText As String = ""
    Private scrollIndex As Integer = 0
    Private Const DISPLAY_CHARS As Integer = 78

    ' System folders and game assets
    '
    ' Each system has its own folder in the app folder, holding everything for it:
    '   C64\Games  C64\Images\Case|Title|Screen  C64\Info  C64\Manuals  C64\Settings
    ' and the same for VIC20 and C16. A folder chosen in that system's Settings is
    ' used as it is, and nothing is created inside it.

    Private Const INFO_FOLDER_PREFIX As String = "InfoFolder="

    Private Shared Function SystemRoot(gameSystemName As String) As String
        Return Path.Combine(Application.StartupPath, gameSystemName)
    End Function

    Private Shared Function SystemSettingsFolder(gameSystemName As String) As String
        Return Path.Combine(SystemRoot(gameSystemName), "Settings")
    End Function

    Private Shared Function ResolveFolder(folder As String) As String
        Return If(Path.IsPathRooted(folder), folder, Path.Combine(Application.StartupPath, folder))
    End Function

    ' Games, Images, Manuals and Info folders for a system, from its settings
    Private Shared Function SystemFolders(gameSystemName As String) As String()
        Dim root As String = SystemRoot(gameSystemName)
        Dim folders As String() = {Path.Combine(root, "Games"), Path.Combine(root, "Images"),
                                   Path.Combine(root, "Manuals"), Path.Combine(root, "Info")}
        Dim settingsPath As String = Path.Combine(SystemSettingsFolder(gameSystemName), "settings.txt")
        If Not File.Exists(settingsPath) Then Return folders
        Try
            Dim lines As String() = File.ReadAllLines(settingsPath)
            For index As Integer = 0 To 2
                If lines.Length > index AndAlso lines(index).Trim() <> "" Then folders(index) = ResolveFolder(lines(index).Trim())
            Next
            Dim infoLine As String = lines.Skip(5).FirstOrDefault(Function(l) l.StartsWith(INFO_FOLDER_PREFIX))
            If infoLine IsNot Nothing AndAlso infoLine.Trim().Length > INFO_FOLDER_PREFIX.Length Then
                folders(3) = ResolveFolder(infoLine.Substring(INFO_FOLDER_PREFIX.Length).Trim())
            End If
        Catch ex As IOException
            Debug.WriteLine($"Could not read {settingsPath}: {ex.Message}")
        End Try
        Return folders
    End Function

    Private Shared Function SameFolder(first As String, second As String) As Boolean
        Try
            Return String.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                                 Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' True for the system's own folder in the app folder, rather than one chosen in Settings
    Private Shared Function IsDefaultFolder(folder As String, gameSystemName As String, name As String) As Boolean
        Return SameFolder(folder, Path.Combine(SystemRoot(gameSystemName), name))
    End Function

    Private Function InfoFolder() As String
        Return _infoFolder
    End Function

    ' Creates each system's own folders and a starting settings file, in the app folder only
    Private Sub EnsureSystemFolders()
        For Each gameSystemName In _systemFolders
            Try
                Dim root As String = SystemRoot(gameSystemName)
                For Each folderName In {"Games", "Info", "Manuals", "Settings"}
                    Directory.CreateDirectory(Path.Combine(root, folderName))
                Next
                For Each typeFolder In _imageTypeFolders
                    Directory.CreateDirectory(Path.Combine(root, "Images", typeFolder))
                Next
                Dim settingsPath As String = Path.Combine(SystemSettingsFolder(gameSystemName), "settings.txt")
                If Not File.Exists(settingsPath) Then File.WriteAllLines(settingsPath, {"", "", "", "True", ""})
            Catch ex As Exception
                Debug.WriteLine($"Could not create {gameSystemName} folders: {ex.Message}")
            End Try
        Next
    End Sub

    Private Shared Function SystemFromName(folderName As String) As String
        Select Case folderName.ToUpperInvariant().Replace("-", "").Replace(" ", "").Replace("/", "")
            Case "VIC20", "VIC", "VC20" : Return "VIC20"
            Case "C16", "PLUS4", "C116", "264", "C16PLUS4" : Return "C16"
            Case "C64", "COMMODORE64", "C64C" : Return "C64"
            Case Else : Return Nothing
        End Select
    End Function

    ' Every game in the list belongs to the system on show
    Private Function GameSystem(gameFile As String) As String
        Return _currentSystem
    End Function

    ' The system whose games folder holds a path, or Nothing
    Private Function SystemOwningFolder(folder As String) As String
        For Each gameSystemName In _systemFolders
            Dim gamesFolder As String = If(gameSystemName = _currentSystem, GameFolder, SystemFolders(gameSystemName)(0))
            Dim full As String = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
            If full.StartsWith(Path.GetFullPath(gamesFolder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase) Then Return gameSystemName
        Next
        Return Nothing
    End Function

    ' A chosen games folder may hold other systems' games in folders named after them
    Private Function InOtherSystemFolder(gameFile As String) As Boolean
        Dim parts = Path.GetRelativePath(GameFolder, gameFile).Split({Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar})
        If parts.Length < 2 Then Return False
        Dim named As String = SystemFromName(parts(0))
        Return named IsNot Nothing AndAlso named <> _currentSystem
    End Function

    ' The machine to tell the backend: the system on show
    Private Function MachineFor(gameFile As String) As String
        Return GameSystem(gameFile).ToLowerInvariant()
    End Function

    ' A place a game's file can live: a folder plus an ending added to the game's name
    Private Class AssetSlot
        Public Folder As String
        Public Suffix As String
        Public Extensions As String()

        Public Sub New(folder As String, suffix As String, extensions As String())
            Me.Folder = folder
            Me.Suffix = suffix
            Me.Extensions = extensions
        End Sub

        Public Function PathFor(gameName As String, ext As String) As String
            Return Path.Combine(Folder, gameName & Suffix & ext)
        End Function

        Public Function Find(gameName As String) As String
            For Each ext In Extensions
                If File.Exists(PathFor(gameName, ext)) Then Return PathFor(gameName, ext)
            Next
            Return Nothing
        End Function
    End Class

    Private Function FoldersOf(gameSystemName As String) As String()
        If gameSystemName = _currentSystem Then Return {GameFolder, ImageFolder, ManualFolder, InfoFolder()}
        Return SystemFolders(gameSystemName)
    End Function

    ' The system's own images folder has Case, Title and Screen subfolders. In a chosen
    ' folder, case images use the game's name and the others add " - Title" or " - Screen".
    Private Function ImageSlot(gameSystemName As String, typeFolder As String) As AssetSlot
        Dim folder As String = FoldersOf(gameSystemName)(1)
        If IsDefaultFolder(folder, gameSystemName, "Images") Then
            Return New AssetSlot(Path.Combine(folder, typeFolder), "", _allowedImageExtensions)
        End If
        Return New AssetSlot(folder, If(typeFolder = "Case", "", " - " & typeFolder), _allowedImageExtensions)
    End Function

    Private Function ManualSlot(gameSystemName As String) As AssetSlot
        Return New AssetSlot(FoldersOf(gameSystemName)(2), "", _allowedManualExtensions)
    End Function

    Private Function InfoSlot(gameSystemName As String) As AssetSlot
        Return New AssetSlot(FoldersOf(gameSystemName)(3), "", {".txt"})
    End Function

    ' Every place that can hold a file for a game, in a fixed order
    Private Function AssetFolders(gameSystemName As String) As List(Of AssetSlot)
        Dim slots As New List(Of AssetSlot)
        For Each typeFolder In _imageTypeFolders
            slots.Add(ImageSlot(gameSystemName, typeFolder))
        Next
        slots.Add(ManualSlot(gameSystemName))
        slots.Add(InfoSlot(gameSystemName))
        Return slots
    End Function

    ' Images loose in the images folder, as in earlier versions, are still shown
    Private Function LegacyAssetFolders() As List(Of AssetSlot)
        Return New List(Of AssetSlot) From {
            New AssetSlot(ImageFolder, "", _allowedImageExtensions),
            New AssetSlot(ManualFolder, "", _allowedManualExtensions)
        }
    End Function

    Private Sub MoveAssets(oldName As String, oldSystem As String, newName As String, newSystem As String)
        ' Tagged manuals, such as "Game (Europe) (Map).pdf", keep their tags
        Dim newManuals As AssetSlot = ManualSlot(newSystem)
        For Each tagged In ManualFilesFor(ManualSlot(oldSystem), oldName).
                Where(Function(f) Not Path.GetFileNameWithoutExtension(f).Equals(oldName, StringComparison.OrdinalIgnoreCase))
            Dim rest As String = Path.GetFileName(tagged).Substring(oldName.Length)
            Dim moved As String = Path.Combine(newManuals.Folder, newName & rest)
            If Not String.Equals(tagged, moved, StringComparison.OrdinalIgnoreCase) Then
                Directory.CreateDirectory(newManuals.Folder)
                SafeRenameFile(tagged, moved)
            End If
        Next
        Dim fromSlots = AssetFolders(oldSystem)
        Dim toSlots = AssetFolders(newSystem)
        For index As Integer = 0 To fromSlots.Count - 1
            For Each ext In fromSlots(index).Extensions
                Dim oldPath As String = fromSlots(index).PathFor(oldName, ext)
                Dim newPath As String = toSlots(index).PathFor(newName, ext)
                If File.Exists(oldPath) AndAlso Not String.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) Then
                    Directory.CreateDirectory(toSlots(index).Folder)
                    SafeRenameFile(oldPath, newPath)
                End If
            Next
        Next
        If oldName <> newName Then
            For Each slot In LegacyAssetFolders()
                For Each ext In slot.Extensions
                    If File.Exists(slot.PathFor(oldName, ext)) Then SafeRenameFile(slot.PathFor(oldName, ext), slot.PathFor(newName, ext))
                Next
            Next
        End If
    End Sub

    ' keepNames: the names (file names and list labels) of games that stay. Their
    ' pictures, info and manuals are left alone, even when they share a name with
    ' the game going: "3D Pool.prg" and "3D Pool.tap" share "3D Pool.png", and
    ' "3D Pool (*" would also catch the manual of "3D Pool (1989)(Firebird)".
    Private Sub RecycleAssets(gameName As String, gameSystemName As String, Optional keepNames As HashSet(Of String) = Nothing)
        If keepNames IsNot Nothing AndAlso keepNames.Contains(gameName) Then Return
        For Each tagged In ManualFilesFor(ManualSlot(gameSystemName), gameName).
                Where(Function(f) Not Path.GetFileNameWithoutExtension(f).Equals(gameName, StringComparison.OrdinalIgnoreCase))
            If keepNames IsNot Nothing Then
                Dim stem As String = Path.GetFileNameWithoutExtension(tagged)
                If keepNames.Any(Function(kept) kept.Length > gameName.Length AndAlso
                                     (stem.Equals(kept, StringComparison.OrdinalIgnoreCase) OrElse
                                      stem.StartsWith(kept & " (", StringComparison.OrdinalIgnoreCase))) Then Continue For
            End If
            PrepareForFileOperation()
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(tagged,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
        Next
        For Each slot In AssetFolders(gameSystemName).Concat(LegacyAssetFolders())
            For Each ext In slot.Extensions
                Dim assetPath As String = slot.PathFor(gameName, ext)
                If File.Exists(assetPath) Then
                    PrepareForFileOperation()
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        assetPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
                End If
            Next
        Next
    End Sub

    Private Function FindInSlots(slots As IEnumerable(Of AssetSlot), names As IEnumerable(Of String)) As String
        For Each slot In slots
            For Each assetName As String In names
                Dim found As String = slot.Find(assetName)
                If found IsNot Nothing Then Return found
            Next
        Next
        Return Nothing
    End Function

        ' Downloading images and information

    ' Lookups that found nothing in the last 30 days, as written by cbmassets.py
    Private Function LoadRecentNotFound() As HashSet(Of String)
        Dim recent As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim listPath As String = Path.Combine(SystemSettingsFolder(_currentSystem), "_not_found.txt")
        If Not File.Exists(listPath) Then Return recent
        Dim cutoff As Double = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds()
        Try
            For Each line In File.ReadAllLines(listPath)
                Dim parts = line.Split(vbTab(0))
                Dim stamp As Double
                If parts.Length = 4 AndAlso Double.TryParse(parts(3), Globalization.NumberStyles.Float,
                                                            Globalization.CultureInfo.InvariantCulture, stamp) AndAlso stamp >= cutoff Then
                    recent.Add($"{parts(0)}|{parts(1)}|{parts(2)}")
                End If
            Next
        Catch ex As IOException
            Debug.WriteLine($"Could not read the not-found list: {ex.Message}")
        End Try
        Return recent
    End Function

    ' True when a game lacks a requested image or its info file. With a not-found list,
    ' anything already looked for without success does not count.
    Private Function IsMissingAssets(gameFile As String, imageKinds As String, includeInfo As Boolean,
                                     notFound As HashSet(Of String)) As Boolean
        Dim gameSystemName As String = GameSystem(gameFile)
        Dim plainName As String = Path.GetFileNameWithoutExtension(gameFile)
        Dim names As String() = {plainName}
        Dim kindFolders As New Dictionary(Of String, String) From {{"case", "Case"}, {"title", "Title"}, {"screen", "Screen"}}
        Dim wanted = imageKinds.Split({","c}, StringSplitOptions.RemoveEmptyEntries).ToList()
        If includeInfo Then wanted.Add("info")

        For Each kind In wanted
            If notFound IsNot Nothing AndAlso notFound.Contains($"{gameSystemName}|{plainName.ToLowerInvariant()}|{kind}") Then Continue For
            Dim slot As AssetSlot = If(kind = "info", InfoSlot(gameSystemName), ImageSlot(gameSystemName, kindFolders(kind)))
            Dim found As String = FindInSlots({slot}, names)
            If found Is Nothing Then Return True
            ' An info file with empty fields is sent too, so the sources can fill them,
            ' unless that was tried recently without finding anything more
            If kind = "info" AndAlso InfoHasGaps(found) AndAlso
               (notFound Is Nothing OrElse Not notFound.Contains($"{gameSystemName}|{plainName.ToLowerInvariant()}|fill")) Then Return True
        Next
        Return False
    End Function

    ' Must match info_gaps in cbmassets.py: an empty or missing Year, Publisher,
    ' Developer, Genre or Players line, or no description
    Private Shared ReadOnly GAP_FIELDS As String() = {"Year", "Publisher", "Developer", "Genre", "Players"}
    Private Const NO_DESCRIPTION As String = "No description found yet. Add your own notes here."

    Private Shared Function InfoHasGaps(infoPath As String) As Boolean
        Dim lines As String()
        Try
            lines = File.ReadAllLines(infoPath)
        Catch ex As IOException
            Return False
        Catch ex As UnauthorizedAccessException
            Return False
        End Try
        Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim description As New System.Text.StringBuilder()
        Dim inAbout As Boolean = False
        For Each line In lines
            Dim trimmed As String = line.Trim()
            If inAbout Then
                If trimmed.StartsWith("Source:", StringComparison.OrdinalIgnoreCase) Then Exit For
                description.Append(trimmed).Append(" "c)
            ElseIf trimmed.StartsWith("About:", StringComparison.OrdinalIgnoreCase) Then
                inAbout = True
                description.Append(trimmed.Substring(6).Trim()).Append(" "c)
            Else
                Dim colon As Integer = trimmed.IndexOf(":"c)
                If colon > 0 Then
                    Dim field As String = trimmed.Substring(0, colon).Trim()
                    If Not values.ContainsKey(field) Then values(field) = trimmed.Substring(colon + 1).Trim()
                End If
            End If
        Next
        For Each field In GAP_FIELDS
            Dim value As String = Nothing
            If Not values.TryGetValue(field, value) OrElse value = "" Then Return True
        Next
        Dim text As String = description.ToString().Trim()
        Return text = "" OrElse text = NO_DESCRIPTION
    End Function

    Private Function MissingAssetJobs(imageKinds As String, includeInfo As Boolean, notFound As HashSet(Of String),
                                      Optional onlyFiles As HashSet(Of String) = Nothing) As List(Of String)
        Dim chosenTitles = ReadNameMatches()
        Return _gameFiles.Values.
            Where(Function(f) (onlyFiles Is Nothing OrElse onlyFiles.Contains(f)) AndAlso IsMissingAssets(f, imageKinds, includeInfo, notFound)).
            Select(Function(f) JobLine(f, chosenTitles)).
            Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    ' system, game, and the title picked for it from a list of maybes if there is one
    Private Function JobLine(gameFile As String, chosenTitles As Dictionary(Of String, String)) As String
        Dim gameName As String = Path.GetFileNameWithoutExtension(gameFile)
        Dim line As String = GameSystem(gameFile) & vbTab & gameName
        Dim chosen As String = Nothing
        If chosenTitles.TryGetValue(gameName, chosen) Then line &= vbTab & chosen
        Return line
    End Function

    ' Titles picked from lists of maybes: game file name, then the title or "-" for none
    Private Function NameMatchesPath() As String
        Return Path.Combine(SystemSettingsFolder(_currentSystem), "name_matches.txt")
    End Function

    Private Function ReadNameMatches() As Dictionary(Of String, String)
        Dim matches As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Try
            If File.Exists(NameMatchesPath()) Then
                For Each line In File.ReadAllLines(NameMatchesPath())
                    Dim parts = line.Split(vbTab(0))
                    If parts.Length = 2 AndAlso parts(0) <> "" AndAlso parts(1) <> "" Then matches(parts(0)) = parts(1)
                Next
            End If
        Catch ex As IOException
            Debug.WriteLine($"Could not read name matches: {ex.Message}")
        End Try
        Return matches
    End Function

    Private Sub SaveNameMatches(chosen As Dictionary(Of String, String))
        Dim matches = ReadNameMatches()
        For Each pair In chosen
            matches(pair.Key) = pair.Value
        Next
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(NameMatchesPath()))
            File.WriteAllLines(NameMatchesPath(), matches.Select(Function(pair) pair.Key & vbTab & pair.Value))
        Catch ex As IOException
            MessageBox.Show($"Could not save the chosen titles: {ex.Message}", "Download", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    ' Games the download could not find by name, each with titles that might be it.
    ' Returns game file name to chosen title ("-" for none of these), or Nothing if cancelled.
    Private Function ChooseMaybeMatches(maybes As List(Of String()), Optional introText As String = Nothing,
                                        Optional firstHeader As String = "Game", Optional matchHeader As String = "Is it...",
                                        Optional showNamesAsTheyAre As Boolean = False) As Dictionary(Of String, String)
        Const SKIP_CHOICE As String = "(skip for now)"
        Const NONE_CHOICE As String = "None of these"
        Using window As New Form() With {
            .Text = "Possible Matches", .Size = New Size(760, 480), .MinimumSize = New Size(480, 300),
            .StartPosition = FormStartPosition.CenterParent, .MinimizeBox = False, .ShowIcon = False,
            .ShowInTaskbar = False, .Font = Me.Font}

            Dim grid As New DataGridView() With {
                .Dock = DockStyle.Fill, .AllowUserToAddRows = False, .AllowUserToDeleteRows = False,
                .AllowUserToResizeRows = False, .RowHeadersVisible = False, .EditMode = DataGridViewEditMode.EditOnEnter,
                .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, .BackgroundColor = SystemColors.Window}
            Dim gameColumn As New DataGridViewTextBoxColumn() With {.HeaderText = firstHeader, .ReadOnly = True, .FillWeight = 45}
            Dim matchColumn As New DataGridViewComboBoxColumn() With {.HeaderText = matchHeader, .FillWeight = 55, .FlatStyle = FlatStyle.Flat}
            grid.Columns.AddRange(gameColumn, matchColumn)
            For Each maybe In maybes
                Dim rowIndex As Integer = grid.Rows.Add()
                Dim row As DataGridViewRow = grid.Rows(rowIndex)
                row.Cells(0).Value = If(showNamesAsTheyAre, maybe(0), CleanTitle(maybe(0)))
                row.Tag = maybe(0)
                Dim cell = DirectCast(row.Cells(1), DataGridViewComboBoxCell)
                cell.Items.Add(SKIP_CHOICE)
                For index As Integer = 1 To maybe.Length - 1
                    If Not cell.Items.Contains(maybe(index)) Then cell.Items.Add(maybe(index))
                Next
                cell.Items.Add(NONE_CHOICE)
                cell.Value = SKIP_CHOICE
            Next
            ' A choice counts as soon as it is picked, not only when the cell is left
            AddHandler grid.CurrentCellDirtyStateChanged,
                Sub(sender, e)
                    If grid.IsCurrentCellDirty Then grid.CommitEdit(DataGridViewDataErrorContexts.Commit)
                End Sub

            Dim intro As New Label() With {
                .Dock = DockStyle.Top, .Height = 44, .Padding = New Padding(8, 8, 8, 0),
                .Text = If(introText, $"{maybes.Count} game(s) were not found by name. Pick the right title for each and its " &
                        "images and info will be downloaded. Games left on ""skip for now"" are asked about again next time; " &
                        """None of these"" stops them being asked.")}
            Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .Height = 42, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(6)}
            Dim cancelChoice As New Button() With {.Text = "Cancel", .DialogResult = DialogResult.Cancel, .AutoSize = True}
            Dim useButton As New Button() With {.Text = "Use These", .DialogResult = DialogResult.OK, .AutoSize = True}
            buttons.Controls.Add(cancelChoice)
            buttons.Controls.Add(useButton)
            window.Controls.Add(grid)
            window.Controls.Add(intro)
            window.Controls.Add(buttons)
            window.AcceptButton = useButton
            window.CancelButton = cancelChoice

            If window.ShowDialog(Me) <> DialogResult.OK Then Return Nothing
            grid.EndEdit()
            Dim chosen As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each row As DataGridViewRow In grid.Rows
                Dim value As String = TryCast(row.Cells(1).Value, String)
                If String.IsNullOrEmpty(value) OrElse value = SKIP_CHOICE Then Continue For
                chosen(CStr(row.Tag)) = If(value = NONE_CHOICE, "-", value)
            Next
            Return chosen
        End Using
    End Function

    Private _lastImageKinds As String = ""
    Private _lastIncludeInfo As Boolean = True

    ' forceRetry: try these games even if they were not found before (after a title was picked)
    Private Sub StartAssetDownload(imageKinds As String, includeInfo As Boolean, Optional onlyFiles As HashSet(Of String) = Nothing,
                                   Optional forceRetry As Boolean = False, Optional sayIfNothing As Boolean = False)
        If _assetProcess IsNot Nothing AndAlso Not _assetProcess.HasExited Then
            MessageBox.Show("A download is already running.", "Download", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        _lastImageKinds = imageKinds
        _lastIncludeInfo = includeInfo

        ' Only games still missing something that has not already been looked for.
        ' Once those are done, the same button tries the ones not found before.
        Dim notFound As HashSet(Of String) = If(forceRetry, Nothing, LoadRecentNotFound())
        Dim retryNotFound As Boolean = forceRetry
        Dim jobs = MissingAssetJobs(imageKinds, includeInfo, notFound, onlyFiles)
        If jobs.Count = 0 AndAlso onlyFiles Is Nothing Then
            jobs = MissingAssetJobs(imageKinds, includeInfo, Nothing)
            retryNotFound = True
        End If
        If jobs.Count = 0 Then
            If onlyFiles Is Nothing Then
                MessageBox.Show("Every game already has these files.", "Download", MessageBoxButtons.OK, MessageBoxIcon.Information)
            ElseIf sayIfNothing Then
                Dim what As String = If(includeInfo, "all its details", "all three pictures")
                MessageBox.Show(If(onlyFiles.Count = 1, $"This game already has {what}.",
                                   $"These games already have {what.Replace("its", "their")}."),
                                "Download", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
            Return
        End If

        Dim jobsFile As String = Path.Combine(Path.GetTempPath(), "cbm_asset_jobs.txt")
        File.WriteAllLines(jobsFile, jobs)

        Dim arguments As String = $"""{Path.Combine(Application.StartupPath, "cbmassets.py")}"" --jobs ""{jobsFile}"" " &
            $"--images-root ""{ImageFolder.TrimEnd(Path.DirectorySeparatorChar)}"" --info-root ""{InfoFolder()}"""
        If imageKinds <> "" Then arguments &= $" --images {imageKinds}"
        If includeInfo Then arguments &= " --info"
        If Not IsDefaultFolder(ImageFolder, _currentSystem, "Images") Then arguments &= " --flat-images"
        arguments &= $" --not-found ""{Path.Combine(SystemSettingsFolder(_currentSystem), "_not_found.txt")}"""
        If retryNotFound Then arguments &= " --retry"

        Dim summary As String = ""
        Dim problems As New List(Of String)
        Dim maybes As New List(Of String())
        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                .Arguments = arguments,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }
            ' TheGamesDB key from Settings, passed privately rather than on the command line
            Dim tgdbKey As String = SettingsForm.ReadTheGamesDBKey()
            If tgdbKey <> "" Then startInfo.Environment("CBM_TGDB_KEY") = tgdbKey
            _assetProcess = New Process() With {.StartInfo = startInfo, .EnableRaisingEvents = True}
            AddHandler _assetProcess.OutputDataReceived,
                Sub(s, args)
                    Dim line As String = args.Data
                    If String.IsNullOrEmpty(line) Then Return
                    If line.StartsWith("Done:") Then
                        summary = line.Substring(5).Trim()
                    ElseIf line.StartsWith("Maybe:") Then
                        Dim parts = line.Substring(6).Trim().Split(vbTab(0))
                        If parts.Length >= 2 Then
                            SyncLock maybes
                                maybes.Add(parts)
                            End SyncLock
                        End If
                    ElseIf line.StartsWith("Warning:") OrElse line.StartsWith("Error:") Then
                        SyncLock problems
                            problems.Add(line)
                        End SyncLock
                    ElseIf line.StartsWith("Note:") Then
                        ' Such as reading a site's whole game list, before the games start
                        ShowActivity(line.Substring(5).Trim())
                    ElseIf line.StartsWith("Progress:") Then
                        Dim progressText As String = line.Substring(9).Trim().Split(" "c)(0)
                        Try
                            Me.BeginInvoke(Sub()
                                               ShowActivity($"Downloading: {progressText.Replace("/", " of ")}")
                                           End Sub)
                        Catch ex As InvalidOperationException
                            ' The form is closing
                        End Try
                    End If
                End Sub
            AddHandler _assetProcess.ErrorDataReceived,
                Sub(s, args)
                    If Not String.IsNullOrEmpty(args.Data) Then
                        SyncLock problems
                            problems.Add(args.Data)
                        End SyncLock
                    End If
                End Sub
            AddHandler _assetProcess.Exited,
                Sub(s, args)
                    Try
                        Me.BeginInvoke(Sub() FinishAssetDownload(summary, problems, maybes))
                    Catch ex As InvalidOperationException
                        ' The form is closing
                    End Try
                End Sub

            _assetProcess.Start()
            _childProcesses.Add(_assetProcess)
            _assetProcess.BeginOutputReadLine()
            _assetProcess.BeginErrorReadLine()
            ShowActivity("Downloading...")
        Catch ex As Exception
            _assetProcess = Nothing
            MessageBox.Show($"Could not start the download: {ex.Message}", "Download", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub FinishAssetDownload(summary As String, problems As List(Of String), maybes As List(Of String()))
        _assetProcess = Nothing
        ShowActivity("")
        LoadGames() ' picks up new years, publishers and genres, and reloads the selected game

        Dim message As String = If(summary = "", "The download stopped before it finished.", $"Finished: {summary}.")
        Dim icon As MessageBoxIcon = MessageBoxIcon.Information
        SyncLock problems
            If problems.Count > 0 Then
                message &= Environment.NewLine & Environment.NewLine & $"{problems.Count} problem(s), first few:" &
                    Environment.NewLine & String.Join(Environment.NewLine, problems.Take(5))
                icon = MessageBoxIcon.Warning
            End If
        End SyncLock
        MessageBox.Show(message, "Download", MessageBoxButtons.OK, icon)

        ' Offer close titles for games not found by name, then fetch the ones picked
        Dim found As List(Of String())
        SyncLock maybes
            found = maybes.ToList()
        End SyncLock
        If found.Count > 0 Then
            Dim chosen = ChooseMaybeMatches(found)
            If chosen IsNot Nothing AndAlso chosen.Count > 0 Then
                SaveNameMatches(chosen)
                Dim picked = chosen.Where(Function(pair) pair.Value <> "-").Select(Function(pair) pair.Key).
                    ToHashSet(StringComparer.OrdinalIgnoreCase)
                Dim files = _gameFiles.Values.Where(Function(f) picked.Contains(Path.GetFileNameWithoutExtension(f))).
                    ToHashSet(StringComparer.OrdinalIgnoreCase)
                If files.Count > 0 Then
                    StartAssetDownload(If(_lastImageKinds = "", "case,title,screen", _lastImageKinds), True, files, forceRetry:=True)
                    Return    ' a waiting import download starts when this one ends
                End If
            End If
        End If
        StartPendingDownload()
    End Sub

    Private Function BuildViewMenu() As ToolStripMenuItem
        Dim viewMenu As New ToolStripMenuItem("View")
        Dim imageMenu As New ToolStripMenuItem("Show Image")
        For index As Integer = 0 To _imageTypeFolders.Length - 1
            Dim typeFolder As String = _imageTypeFolders(index)
            Dim item As New ToolStripMenuItem(_imageTypeNames(index)) With {.Checked = (_imageTypeOption = typeFolder)}
            AddHandler item.Click, Sub(sender, e)
                                       _imageTypeOption = typeFolder
                                       For Each other As ToolStripMenuItem In imageMenu.DropDownItems
                                           other.Checked = (other Is sender)
                                       Next
                                       SaveTapeOptions()
                                       If ListBox1.SelectedItem IsNot Nothing Then LoadGameAssets(ListBox1.SelectedItem.ToString())
                                   End Sub
            imageMenu.DropDownItems.Add(item)
        Next
        viewMenu.DropDownItems.Add(imageMenu)

        ' The same choice as the dropdown by the search box: all games, favourites or a genre
        Dim gamesMenu As New ToolStripMenuItem("Show Games")
        gamesMenu.DropDownItems.Add(New ToolStripMenuItem("(loading)"))
        AddHandler gamesMenu.DropDownOpening, Sub(sender, e)
                                                  gamesMenu.DropDownItems.Clear()
                                                  If _viewCombo Is Nothing Then Return
                                                  Dim current As Object = _viewCombo.SelectedItem
                                                  Dim genreMenu As New ToolStripMenuItem("Genre")
                                                  For Each choice As Object In _viewCombo.Items
                                                      If TypeOf choice Is ViewHeading Then Continue For
                                                      Dim picked As Object = choice
                                                      Dim isGenre As Boolean = TypeOf choice Is GenreChoice
                                                      Dim item As New ToolStripMenuItem(choice.ToString()) With {.Checked = choice Is current}
                                                      AddHandler item.Click, Sub(s, args) _viewCombo.SelectedItem = picked
                                                      If isGenre Then
                                                          genreMenu.DropDownItems.Add(item)
                                                      Else
                                                          gamesMenu.DropDownItems.Add(item)
                                                      End If
                                                  Next
                                                  If genreMenu.DropDownItems.Count = 0 Then
                                                      genreMenu.DropDownItems.Add(New ToolStripMenuItem("No genres yet: download game info first") With {.Enabled = False})
                                                  End If
                                                  genreMenu.Checked = TypeOf current Is GenreChoice
                                                  gamesMenu.DropDownItems.Add(genreMenu)
                                              End Sub
        viewMenu.DropDownItems.Add(gamesMenu)
        Return viewMenu
    End Function

    Private Sub AddDownloadMenuItems(editMenu As ToolStripMenuItem)
        editMenu.DropDownItems.Add(New ToolStripSeparator())

        Dim importItem As New ToolStripMenuItem("Import Games From Folder…")
        AddHandler importItem.Click, Sub(sender, e) ImportGamesFromFolder()
        editMenu.DropDownItems.Add(importItem)
        _downloadMenuItems.Add(importItem)

        Dim importManualsItem As New ToolStripMenuItem("Import Manuals From Folder…")
        AddHandler importManualsItem.Click, Sub(sender, e) ImportManualsFromFolder()
        editMenu.DropDownItems.Add(importManualsItem)
        _downloadMenuItems.Add(importManualsItem)

        Dim imagesMenu As New ToolStripMenuItem("Download Images (All Games)")
        Dim choices = {
            New KeyValuePair(Of String, String)("Game Case", "case"),
            New KeyValuePair(Of String, String)("Title Screen", "title"),
            New KeyValuePair(Of String, String)("Game Screen", "screen"),
            New KeyValuePair(Of String, String)("All Three", "case,title,screen")
        }
        For Each choice In choices
            Dim kinds As String = choice.Value
            Dim item As New ToolStripMenuItem(choice.Key)
            AddHandler item.Click, Sub(sender, e) StartAssetDownload(kinds, False)
            imagesMenu.DropDownItems.Add(item)
        Next
        editMenu.DropDownItems.Add(imagesMenu)

        Dim infoItem As New ToolStripMenuItem("Download Game Info (All Games)")
        AddHandler infoItem.Click, Sub(sender, e) StartAssetDownload("", True)
        editMenu.DropDownItems.Add(infoItem)

        ' Info and all three pictures in one run, such as overnight
        Dim everythingItem As New ToolStripMenuItem("Download Info and Images (All Games)")
        AddHandler everythingItem.Click, Sub(sender, e) StartAssetDownload("case,title,screen", True)
        editMenu.DropDownItems.Add(everythingItem)

        ' Lists games that exist for a system but are not in the games folder; downloads nothing
        Dim missingMenu As New ToolStripMenuItem("Find Missing Games")
        AddHandler missingMenu.Click, Sub(sender, e) FindMissingGames(_currentSystem, _systemNames(_currentSystem))
        editMenu.DropDownItems.Add(New ToolStripSeparator())
        editMenu.DropDownItems.Add(missingMenu)

        _downloadMenuItems.AddRange({imagesMenu, infoItem, everythingItem, missingMenu})
        For Each item In _downloadMenuItems
            item.Enabled = _editModeEnabled
        Next
    End Sub

    ' Missing games: titles the image collections know about that are not in the games folder

    Private Async Sub FindMissingGames(gameSystemName As String, systemLabel As String)
        Dim haveFile As String = Path.Combine(Path.GetTempPath(), "cbm_have_games.txt")
        Dim outFile As String = Path.Combine(Path.GetTempPath(), "cbm_missing_games.txt")
        File.WriteAllLines(haveFile, _gameFiles.Values.
            Where(Function(f) GameSystem(f) = gameSystemName).
            Select(Function(f) gameSystemName & vbTab & Path.GetFileNameWithoutExtension(f)).
            Distinct(StringComparer.OrdinalIgnoreCase))
        If File.Exists(outFile) Then File.Delete(outFile)

        ShowActivity($"Checking {systemLabel} games...")
        Dim arguments As String = $"""{Path.Combine(Application.StartupPath, "cbmassets.py")}"" --jobs ""{haveFile}"" --missing {gameSystemName} --out ""{outFile}"""
        Dim output As String = Await Task.Run(Function()
                                                  Try
                                                      Dim startInfo As New ProcessStartInfo() With {
                                                          .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                                                          .Arguments = arguments,
                                                          .UseShellExecute = False,
                                                          .CreateNoWindow = True,
                                                          .RedirectStandardError = True,
                                                          .RedirectStandardOutput = True
                                                      }
                                                      Using checkProcess As Process = Process.Start(startInfo)
                                                          Dim errorText = checkProcess.StandardError.ReadToEndAsync()
                                                          Dim text As String = checkProcess.StandardOutput.ReadToEnd()
                                                          checkProcess.WaitForExit()
                                                          Return text & errorText.Result
                                                      End Using
                                                  Catch ex As Exception
                                                      Return "Error: " & ex.Message
                                                  End Try
                                              End Function)
        If Me.IsDisposed Then Return
        ShowActivity("")

        If Not File.Exists(outFile) Then
            Dim problem = output.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).
                FirstOrDefault(Function(l) l.StartsWith("Error:"))
            MessageBox.Show(If(problem, output.Trim()), "Find Missing Games", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Dim rows = File.ReadAllLines(outFile).Select(Function(l) l.Split(vbTab(0))).Where(Function(p) p.Length = 3).ToList()
        ShowMissingGames(systemLabel, rows)
    End Sub

    ' Name matching, the same way cbmassets.py matches names

    Private Shared ReadOnly _articles As New HashSet(Of String) From {"the", "a", "an", "der", "die", "das", "le", "la", "les"}
    Private Shared ReadOnly _roman As New Dictionary(Of String, String) From {{"ii", "2"}, {"iii", "3"}, {"iv", "4"}, {"v", "5"}, {"vi", "6"}}

    Private Shared Function NameKey(title As String) As String
        Dim lower As String = title.ToLowerInvariant().Replace("&", " and ")
        lower = System.Text.RegularExpressions.Regex.Replace(lower, ",\s*(the|a|an|der|die|das|le|la|les)$", "")
        Dim words = System.Text.RegularExpressions.Regex.Matches(lower, "[a-z0-9]+").
            Cast(Of System.Text.RegularExpressions.Match)().
            Select(Function(m) If(_roman.ContainsKey(m.Value), _roman(m.Value), m.Value)).ToList()
        words = DropMachineWords(words)
        While words.Count > 1 AndAlso _articles.Contains(words(0))
            words.RemoveAt(0)
        End While
        Return String.Concat(words)
    End Function

    ' The title's key, plus the key of the part before any subtitle
    Private Shared Function NameKeys(name As String) As List(Of String)
        Dim title As String = SplitTitleMarks(System.Text.RegularExpressions.Regex.Split(name, "\s*[(\[]")(0).Trim()).Core
        Dim keys As New List(Of String) From {NameKey(title)}
        For Each separator In {" - ", ": "}
            If title.Contains(separator) Then keys.Add(NameKey(title.Substring(0, title.IndexOf(separator))))
        Next
        Return keys.Where(Function(k) k <> "").Distinct().ToList()
    End Function

    ' Tape images first, then programs, then recordings, when a game is found in several forms
    Private Shared Function FormatRank(filePath As String) As Integer
        Dim order As String() = {".tap", ".tzx", ".t64", ".prg", ".p00", ".csw", ".gz", ".wav", ".flac", ".mp3", ".ogg"}
        Dim index As Integer = Array.IndexOf(order, Path.GetExtension(filePath).ToLowerInvariant())
        Return If(index < 0, order.Length, index)
    End Function

    Private Sub ShowMissingGames(systemLabel As String, rows As List(Of String()))
        Dim window As New Form() With {
            .Text = $"Missing {systemLabel} Games",
            .Icon = Me.Icon,
            .Size = New Size(760, 600),
            .MinimumSize = New Size(640, 400),
            .StartPosition = FormStartPosition.CenterParent,
            .MinimizeBox = False
        }
        Dim filterBox As New TextBox() With {.Dock = DockStyle.Top, .PlaceholderText = "Filter titles, years, publishers or found files"}
        Dim list As New ListView() With {.Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True,
                                         .GridLines = True, .CheckBoxes = True}
        list.Columns.Add("Add", 40)
        list.Columns.Add("Title", 330)
        Dim yearColumn As ColumnHeader = list.Columns.Add("Year", 170)
        list.Columns.Add("Publisher", 150)

        Dim bottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = 64}
        Dim countLabel As New Label() With {.AutoSize = True, .Location = New Point(8, 8)}
        Dim foundOnly As New CheckBox() With {.Text = "Found only", .AutoSize = True, .Location = New Point(8, 34), .Enabled = False}
        Dim saveButton As New Button() With {.Text = "Save List...", .Width = 90}
        Dim importAllButton As New Button() With {.Text = "Import All", .Width = 90, .Enabled = False}
        Dim importButton As New Button() With {.Text = "Import", .Width = 90, .Enabled = False}
        Dim browseButton As New Button() With {.Text = "Browse...", .Width = 90}
        Dim buttons As Button() = {browseButton, importButton, importAllButton, saveButton}
        For index As Integer = 0 To buttons.Length - 1
            buttons(index).Anchor = AnchorStyles.Right Or AnchorStyles.Top
            buttons(index).Location = New Point(bottom.Width - (buttons.Length - index) * 96 - 4, 30)
        Next
        bottom.Controls.Add(countLabel)
        bottom.Controls.Add(foundOnly)
        bottom.Controls.AddRange(buttons)

        ' Files found for each missing title, best format first
        Dim found As New Dictionary(Of String(), List(Of String))
        Dim imported As New HashSet(Of String())
        Dim ticked As New HashSet(Of String())
        Dim updating As Boolean = False

        Dim showRows = Sub()
                           Dim terms = filterBox.Text.ToLowerInvariant().Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                           updating = True
                           list.BeginUpdate()
                           list.Items.Clear()
                           For Each row In rows
                               Dim files As List(Of String) = Nothing
                               Dim isFound As Boolean = found.TryGetValue(row, files)
                               If foundOnly.Checked AndAlso Not isFound Then Continue For

                               ' Before a scan the column shows the year; after one it shows the file found
                               Dim yearText As String = If(found.Count > 0 OrElse yearColumn.Text = "Found", "", row(1))
                               If isFound Then
                                   yearText = Path.GetFileName(files(0)) & If(files.Count > 1, $" (+{files.Count - 1})", "")
                               End If
                               Dim searchText As String = String.Join(" ", {row(0), yearText, row(1), row(2)}).ToLowerInvariant()
                               If Not terms.All(Function(term) searchText.Contains(term)) Then Continue For

                               Dim item As New ListViewItem({"", row(0), yearText, row(2)}) With {.Tag = row, .UseItemStyleForSubItems = True}
                               item.Checked = ticked.Contains(row)
                               If isFound AndAlso Not imported.Contains(row) Then
                                   item.Font = New Font(list.Font, FontStyle.Bold)
                               ElseIf found.Count > 0 Then
                                   item.ForeColor = Color.Gray
                               End If
                               list.Items.Add(item)
                           Next
                           list.EndUpdate()
                           updating = False

                           Dim waiting As Integer = found.Count
                           countLabel.Text = $"{rows.Count:N0} known {systemLabel} games are not in your games folder" &
                               If(found.Count > 0 OrElse yearColumn.Text = "Found", $". {waiting:N0} found to import", "") &
                               If(list.Items.Count <> rows.Count, $" ({list.Items.Count:N0} shown)", "")
                           importButton.Enabled = waiting > 0
                           importAllButton.Enabled = waiting > 0
                       End Sub

        ' Only games with a file found can be ticked
        AddHandler list.ItemCheck, Sub(sender, e)
                                       If updating Then Return
                                       Dim row As String() = DirectCast(list.Items(e.Index).Tag, String())
                                       If Not found.ContainsKey(row) OrElse imported.Contains(row) Then e.NewValue = e.CurrentValue
                                   End Sub

        ' Ticks are remembered, so filtering the list does not lose them
        AddHandler list.ItemChecked, Sub(sender, e)
                                         If updating Then Return
                                         Dim row As String() = DirectCast(e.Item.Tag, String())
                                         If e.Item.Checked Then ticked.Add(row) Else ticked.Remove(row)
                                     End Sub

        AddHandler browseButton.Click,
            Sub(sender, e)
                Using folderDialog As New FolderBrowserDialog() With {.Description = "Choose a folder of games to check against the missing list"}
                    If folderDialog.ShowDialog(window) <> DialogResult.OK Then Return

                    window.Cursor = Cursors.WaitCursor
                    Try
                        ' Index the missing titles by name key
                        Dim byKey As New Dictionary(Of String, List(Of String()))
                        For Each row In rows
                            For Each key In NameKeys(row(0))
                                If Not byKey.ContainsKey(key) Then byKey(key) = New List(Of String())
                                byKey(key).Add(row)
                            Next
                        Next

                        found.Clear()
                        ticked.Clear()
                        Dim candidates = Directory.EnumerateFiles(folderDialog.SelectedPath, "*", SearchOption.AllDirectories).
                            Where(Function(f) IsGameFile(f))
                        For Each candidate In candidates
                            For Each key In NameKeys(Path.GetFileNameWithoutExtension(candidate))
                                Dim matches As List(Of String()) = Nothing
                                If Not byKey.TryGetValue(key, matches) Then Continue For
                                For Each row In matches
                                    If Not found.ContainsKey(row) Then found(row) = New List(Of String)
                                    If Not found(row).Contains(candidate, StringComparer.OrdinalIgnoreCase) Then found(row).Add(candidate)
                                Next
                            Next
                        Next
                        For Each files In found.Values
                            files.Sort(Function(a, b) If(FormatRank(a) <> FormatRank(b), FormatRank(a).CompareTo(FormatRank(b)),
                                                         String.Compare(a, b, StringComparison.OrdinalIgnoreCase)))
                        Next

                        yearColumn.Text = "Found"
                        foundOnly.Enabled = found.Count > 0
                        foundOnly.Checked = found.Count > 0
                        showRows()
                        If found.Count = 0 Then
                            MessageBox.Show(window, "No files in that folder match a missing game.", "Find Missing Games",
                                            MessageBoxButtons.OK, MessageBoxIcon.Information)
                        End If
                    Catch ex As Exception
                        MessageBox.Show(window, $"Could not read that folder: {ex.Message}", "Find Missing Games",
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Finally
                        window.Cursor = Cursors.Default
                    End Try
                End Using
            End Sub

        ' Copies a found game into this system's games folder. The best format is taken,
        ' with any other files of that format for the same title, such as side B.
        Dim importRows = Async Sub(chosen As List(Of String()))
                             Dim pairs As New List(Of KeyValuePair(Of String, String))
                             For Each row In chosen
                                 Dim files = found(row)
                                 Dim bestExtension As String = Path.GetExtension(files(0))
                                 For Each source In files.Where(Function(f) String.Equals(Path.GetExtension(f), bestExtension, StringComparison.OrdinalIgnoreCase))
                                     pairs.Add(New KeyValuePair(Of String, String)(source, Path.Combine(GameFolder, Path.GetFileName(source))))
                                 Next
                                 imported.Add(row)
                                 ticked.Remove(row)
                             Next
                             importButton.Enabled = False
                             importAllButton.Enabled = False
                             ' Copied in the background; the summary message comes from there
                             Await CopyGamesInBackground(pairs)
                             ' Imported games are no longer missing
                             rows.RemoveAll(Function(gone) imported.Contains(gone))
                             For Each row In imported
                                 found.Remove(row)
                             Next
                             imported.Clear()
                             If Not window.IsDisposed Then showRows()
                         End Sub

        AddHandler importButton.Click,
            Sub(sender, e)
                Dim chosen = ticked.Where(Function(row) found.ContainsKey(row) AndAlso Not imported.Contains(row)).ToList()
                If chosen.Count = 0 Then
                    MessageBox.Show(window, "Tick the found games to import first, or use Import All.", "Import",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information)
                    Return
                End If
                importRows(chosen)
            End Sub
        AddHandler importAllButton.Click,
            Sub(sender, e) importRows(found.Keys.Where(Function(row) Not imported.Contains(row)).ToList())

        AddHandler filterBox.TextChanged, Sub(sender, e) showRows()
        AddHandler foundOnly.CheckedChanged, Sub(sender, e) showRows()
        AddHandler saveButton.Click, Sub(sender, e)
                                         Using saveDialog As New SaveFileDialog() With {
                                             .Filter = "Text files (*.txt)|*.txt",
                                             .FileName = $"Missing {systemLabel} games.txt"
                                         }
                                             If saveDialog.ShowDialog(window) = DialogResult.OK Then
                                                 File.WriteAllLines(saveDialog.FileName,
                                                     list.Items.Cast(Of ListViewItem)().
                                                     Select(Function(item) String.Join(vbTab, item.SubItems.Cast(Of ListViewItem.ListViewSubItem)().Skip(1).Select(Function(cell) cell.Text))))
                                             End If
                                         End Using
                                     End Sub

        window.Controls.Add(list)
        window.Controls.Add(filterBox)
        window.Controls.Add(bottom)
        showRows()
        window.ShowDialog(Me)
        window.Dispose()
    End Sub

    ' Choosing the system on show

    Private Function SystemTitle() As String
        Dim gameCount As String = $"{_allGames.Count:N0} game{If(_allGames.Count = 1, "", "s")}"
        If ListBox1.Items.Count <> _allGames.Count Then gameCount = $"{ListBox1.Items.Count:N0} of {gameCount} shown"
        Return $"Commodore Game Loader - v{APP_VERSION} - {_systemNames(_currentSystem)} - {gameCount}"
    End Function

    Private Function BuildSystemMenu() As ToolStripMenuItem
        Dim systemMenu As New ToolStripMenuItem("System")
        For Each choice In _systemNames
            Dim systemKey As String = choice.Key
            Dim item As New ToolStripMenuItem(choice.Value) With {.Checked = (systemKey = _currentSystem)}
            AddHandler item.Click, Sub(sender, e)
                                       If SwitchSystem(systemKey) Then
                                           For Each other As ToolStripMenuItem In systemMenu.DropDownItems
                                               other.Checked = (other Is sender)
                                           Next
                                       End If
                                   End Sub
            systemMenu.DropDownItems.Add(item)
        Next
        Return systemMenu
    End Function

    Private Function SwitchSystem(gameSystemName As String) As Boolean
        If gameSystemName = _currentSystem Then Return True
        If IsTapePlaying() Then
            MessageBox.Show("Stop the tape before changing system.", "System", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return False
        End If

        SaveShownText()
        SaveLastGame()
        SaveTapeOptions()                  ' keeps the old system's tape options
        _currentSystem = gameSystemName
        UseTapeOptionsOf(gameSystemName)
        SaveTapeOptions()

        _fastCheckCache.Clear()
        _tapeTimes.Clear()
        _tapeMemory.Clear()
        _fastCheckFailures.Clear()
        LoadSettings()
        LoadFastScanResults()

        Me.Text = SystemTitle()
        ResetBlockLabels()
        _lastSelectedGame = ""
        ListBox1.ClearSelected()
        If _ramCombo IsNot Nothing Then
            _updatingView = True
            _ramCombo.Items.Clear()
            _ramCombo.Items.Add(RAM_ALL)
            _ramCombo.SelectedIndex = 0
            _updatingView = False
        End If
        If _machineCombo IsNot Nothing Then
            _updatingView = True
            _machineCombo.SelectedIndex = 0
            _updatingView = False
        End If
        PlaceFilterControls()        ' the RAM box comes and goes with the system
        LoadGames()
        _listHeader?.Invalidate()   ' and so does the RAM column
        RestoreLastGame()
        Return True
    End Function

    Private Sub RestoreLastGame()
        If File.Exists(_settingsFilePath) Then
            Dim lines() As String = File.ReadAllLines(_settingsFilePath)
            Dim remember As Boolean
            If lines.Length >= 5 AndAlso Boolean.TryParse(lines(3), remember) AndAlso remember AndAlso
               Not String.IsNullOrEmpty(lines(4)) AndAlso _allGames.Contains(lines(4)) Then
                Dim index = ListBox1.Items.IndexOf(lines(4))
                If index >= 0 Then
                    ListBox1.SelectedIndex = index
                    ' Force load the assets for the selected game
                    _lastSelectedGame = ""
                    ListBox1_SelectedIndexChanged(ListBox1, EventArgs.Empty)
                    Return
                End If
            End If
        End If

        ' If no last game or not found, select first item if available
        If ListBox1.Items.Count > 0 Then
            ListBox1.SelectedIndex = 0
        Else
            PictureBox1.Image = Nothing
            RichTextBox1.Text = ""
            ShowFastLoadStatus("")
        End If
    End Sub

    ' ===== ONE-OFF MOVE TO THE PER-SYSTEM LAYOUT (v1.6.0) =====
    ' Runs once, when settings.txt is still in the app folder from an earlier version.
    ' Remove this section in the next version.

    Private Sub MoveToSystemLayout()
        Dim app As String = Application.StartupPath
        Dim oldSettings As String = Path.Combine(app, "settings.txt")
        If Not File.Exists(oldSettings) Then Return

        Try
            Dim oldLines As String() = File.ReadAllLines(oldSettings)
            Dim lineOf = Function(index As Integer) If(oldLines.Length > index, oldLines(index).Trim(), "")
            Dim oldGames As String = If(lineOf(0) = "", Path.Combine(app, "Games"), ResolveFolder(lineOf(0)))
            Dim oldImages As String = If(lineOf(1) = "", Path.Combine(app, "Images"), ResolveFolder(lineOf(1)))
            Dim oldManuals As String = If(lineOf(2) = "", Path.Combine(app, "Manuals"), ResolveFolder(lineOf(2)))
            Dim oldInfo As String = Path.Combine(app, "Info")
            Dim insideApp = Function(folder As String) Path.GetFullPath(folder).StartsWith(
                Path.GetFullPath(app).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

            EnsureSystemFolders()

            ' Games, images, info and manuals from inside the app folder move into each
            ' system's own folders. Anything not in a system subfolder was shown as C64.
            Dim customGames As New Dictionary(Of String, String)
            For Each pair In {New KeyValuePair(Of String, String)(oldGames, "Games"),
                              New KeyValuePair(Of String, String)(oldImages, "Images"),
                              New KeyValuePair(Of String, String)(oldManuals, "Manuals"),
                              New KeyValuePair(Of String, String)(oldInfo, "Info")}
                Dim source As String = pair.Key
                If Not Directory.Exists(source) OrElse SameFolder(source, app) Then Continue For

                If insideApp(source) Then
                    For Each gameSystemName In _systemFolders
                        MoveFolderContents(Path.Combine(source, gameSystemName), Path.Combine(SystemRoot(gameSystemName), pair.Value))
                    Next
                    If pair.Value = "Info" AndAlso File.Exists(Path.Combine(source, "_not_found.txt")) Then
                        SplitBySystem(Path.Combine(source, "_not_found.txt"), 0, "_not_found.txt")
                    End If
                    MoveFolderContents(source, Path.Combine(SystemRoot("C64"), pair.Value))
                ElseIf pair.Value = "Games" Then
                    ' A games folder outside the app is kept where it is: C64 uses it, and
                    ' its system subfolders, if they hold anything, become VIC20 and C16
                    customGames("C64") = source
                    For Each gameSystemName In {"VIC20", "C16"}
                        Dim subfolder As String = Path.Combine(source, gameSystemName)
                        If Directory.Exists(subfolder) AndAlso Directory.EnumerateFileSystemEntries(subfolder).Any() Then
                            customGames(gameSystemName) = subfolder
                        ElseIf Directory.Exists(subfolder) Then
                            Directory.Delete(subfolder)
                        End If
                    Next
                    Dim c64Subfolder As String = Path.Combine(source, "C64")
                    If Directory.Exists(c64Subfolder) AndAlso Not Directory.EnumerateFileSystemEntries(c64Subfolder).Any() Then
                        Directory.Delete(c64Subfolder)
                    End If
                End If
            Next

            ' Per-game settings go to the system that has the game
            Dim remember As String = If(lineOf(3) = "", "True", lineOf(3))
            Dim lastGame As String = lineOf(4)
            Dim systemLines As New Dictionary(Of String, List(Of String))
            For Each gameSystemName In _systemFolders
                Dim games As String = If(customGames.ContainsKey(gameSystemName), customGames(gameSystemName), "")
                systemLines(gameSystemName) = New List(Of String) From {games, "", "", remember, ""}
            Next
            ' Each system now has its own list, so the [VIC20] style tags are no longer added
            If lastGame <> "" Then systemLines(SystemOfGame(PlainLabel(lastGame), customGames))(4) = PlainLabel(lastGame)
            For Each line In oldLines.Skip(5)
                Dim entry As String = If(line.StartsWith(FAST_LOAD_PREFIX), line.Substring(FAST_LOAD_PREFIX.Length),
                                         If(line.StartsWith(OLD_FAST_LOAD_OFF_PREFIX), line.Substring(OLD_FAST_LOAD_OFF_PREFIX.Length), Nothing))
                If entry Is Nothing Then Continue For
                Dim label As String = If(entry.Contains("|"c), entry.Substring(0, entry.LastIndexOf("|"c)), entry)
                Dim rest As String = entry.Substring(label.Length)
                Dim prefix As String = line.Substring(0, line.Length - entry.Length)
                systemLines(SystemOfGame(PlainLabel(label), customGames)).Add(prefix & PlainLabel(label) & rest)
            Next
            For Each pair In systemLines
                File.WriteAllLines(Path.Combine(SystemSettingsFolder(pair.Key), "settings.txt"), pair.Value)
            Next

            ' Scan results and game locations go to each system's Settings folder
            Dim scanFile As String = Path.Combine(app, "fastload_scan.txt")
            If File.Exists(scanFile) Then SplitBySystem(scanFile, -1, "fastload_scan.txt")
            Dim locationsFile As String = Path.Combine(app, "game_locations.txt")
            If File.Exists(locationsFile) Then SplitBySystem(locationsFile, 1, "game_locations.txt")

            File.Move(oldSettings, Path.Combine(app, "settings_before_v1.6.txt"), True)
        Catch ex As Exception
            MessageBox.Show($"Moving to the per-system folders did not finish: {ex.Message}" & Environment.NewLine &
                            "Nothing has been deleted. The move will be tried again next time the app starts.",
                            "Commodore Game Loader", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    ' Moves everything in a folder into another, keeping what is already there, then
    ' removes the source folder if it ends up empty
    Private Shared Sub MoveFolderContents(source As String, destination As String)
        If Not Directory.Exists(source) Then Return
        If SameFolder(source, destination) Then Return
        Directory.CreateDirectory(destination)
        For Each filePath In Directory.GetFiles(source)
            Dim target As String = Path.Combine(destination, Path.GetFileName(filePath))
            If Not File.Exists(target) Then File.Move(filePath, target)
        Next
        For Each folder In Directory.GetDirectories(source)
            Dim target As String = Path.Combine(destination, Path.GetFileName(folder))
            ' Never move a system's own folder into itself
            If Path.GetFullPath(target).StartsWith(Path.GetFullPath(folder) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Continue For
            If Directory.Exists(target) Then
                MoveFolderContents(folder, target)
            Else
                Directory.Move(folder, target)
            End If
        Next
        If Not Directory.EnumerateFileSystemEntries(source).Any() Then Directory.Delete(source)
    End Sub

    ' Splits a tab-separated file into each system's Settings folder, by the system named
    ' in one column (or, for scan results, the machine at the end of the key)
    Private Sub SplitBySystem(sourceFile As String, column As Integer, fileName As String)
        Dim bySystem As New Dictionary(Of String, List(Of String))
        For Each gameSystemName In _systemFolders
            bySystem(gameSystemName) = New List(Of String)
        Next
        For Each line In File.ReadAllLines(sourceFile)
            Dim parts = line.Split(vbTab(0))
            Dim named As String
            If column < 0 Then
                named = SystemFromName(parts(0).Split("|"c).Last())
            Else
                named = If(parts.Length > column, SystemFromName(parts(column)), Nothing)
            End If
            bySystem(If(named, "C64")).Add(line)
        Next
        For Each pair In bySystem
            If pair.Value.Count > 0 Then File.AppendAllLines(Path.Combine(SystemSettingsFolder(pair.Key), fileName), pair.Value)
        Next
        File.Delete(sourceFile)
    End Sub

    ' A list label without the [C64] or (2) tags added for same-named games
    Private Shared Function PlainLabel(label As String) As String
        Return System.Text.RegularExpressions.Regex.Replace(label, "(\s\[[^\]]+\])?(\s\(\d+\))?$", "")
    End Function

    Private Function SystemOfGame(gameName As String, customGames As Dictionary(Of String, String)) As String
        For Each gameSystemName In {"VIC20", "C16", "C64"}
            Dim folder As String = If(customGames.ContainsKey(gameSystemName), customGames(gameSystemName),
                                      Path.Combine(SystemRoot(gameSystemName), "Games"))
            If Directory.Exists(folder) AndAlso Directory.EnumerateFiles(folder, gameName & ".*", SearchOption.AllDirectories).Any() Then
                Return gameSystemName
            End If
        Next
        Return "C64"
    End Function

    ' ===== END OF ONE-OFF MOVE =====

    ' Game list: names shown without bracketed tags, with Year, Publisher and Genre columns,
    ' and a view of All Games, Favourites or one genre

    Private Class GameDetails
        Public Year As String = ""
        Public Publisher As String = ""
        Public Genre As String = ""
        Public Memory As String = ""      ' from a "Memory:" line, such as 16K or 64K on the C16
    End Class

    Private Const FAVOURITE_PREFIX As String = "Favourite="
    Private Const STOPS_ON_PREFIX As String = "StopsOn="
    Private Const NO_STOPS_PREFIX As String = "NoStops="      ' from 1.19 to 1.24, when stopping was on by default
    Private Const VIEW_ALL As String = "All Games"
    Private Const VIEW_FAVOURITES As String = "Favourites"
    Private ReadOnly _favourites As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    ' Games whose tape stops between parts (off unless turned on for the game). VIC-20
    ' games fast loaded in several parts stop anyway: their loader needs it.
    Private ReadOnly _stopsOn As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _gameDetails As New Dictionary(Of String, GameDetails)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _infoCache As New Dictionary(Of String, KeyValuePair(Of Long, GameDetails))(StringComparer.OrdinalIgnoreCase)
    Private _listHeader As Panel = Nothing
    Private _viewCombo As ComboBox = Nothing
    Private _updatingView As Boolean = False
    Private _lastViewIndex As Integer = 0
    Private _ramCombo As ComboBox = Nothing
    Private Const RAM_ALL As String = "All RAM"
    Private Const MACHINE_ALL As String = "All Machines"
    Private _machineCombo As ComboBox = Nothing
    Private Const RAM_UNKNOWN As String = "Unknown"
    Private _filterRightGap As Integer = 0          ' from the list's right edge to the filter boxes'

    ' A heading in the view list, such as "Genres": drawn with a line above it, never chosen
    Private Class ViewHeading
        Private ReadOnly _caption As String
        Public Sub New(value As String)
            _caption = value
        End Sub
        Public Overrides Function ToString() As String
            Return _caption
        End Function
    End Class

    ' A genre in the view list
    Private Class GenreChoice
        Public ReadOnly Name As String
        Public Sub New(value As String)
            Name = value
        End Sub
        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    ' Titles typed in Edit Details, shown in the list instead of the file's name
    Private ReadOnly _titleOverrides As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly Property TitlesPath As String
        Get
            Return Path.Combine(SystemSettingsFolder(_currentSystem), "titles.txt")
        End Get
    End Property

    Private Sub LoadTitleOverrides()
        _titleOverrides.Clear()
        Try
            If File.Exists(TitlesPath) Then
                For Each line In File.ReadAllLines(TitlesPath)
                    Dim parts = line.Split(vbTab(0))
                    If parts.Length = 2 AndAlso parts(0) <> "" AndAlso parts(1).Trim() <> "" Then _titleOverrides(parts(0)) = parts(1).Trim()
                Next
            End If
        Catch ex As IOException
            Debug.WriteLine($"Could not read titles: {ex.Message}")
        End Try
    End Sub

    Private Sub SaveTitleOverrides()
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(TitlesPath))
            File.WriteAllLines(TitlesPath, _titleOverrides.Select(Function(pair) pair.Key & vbTab & pair.Value))
        Catch ex As IOException
            MessageBox.Show($"Could not save the title: {ex.Message}", "Edit Details", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub
    Private Const HEADER_HEIGHT As Integer = 18
    Private Const YEAR_COLUMN_WIDTH As Integer = 48
    Private Const RAM_COLUMN_WIDTH As Integer = 62

    ' The RAM column is shown for the VIC-20 and the C16, not the C64
    Private ReadOnly Property ShowMemoryColumn As Boolean
        Get
            Return ScanFindsMemory OrElse String.Equals(_currentSystem, "C16", StringComparison.OrdinalIgnoreCase)
        End Get
    End Property

    ' The VIC-20's RAM needs come from the background scan of its tapes; the C16's
    ' (16K or 64K) from the "Memory:" line of its info, which Plus/4 World supplies
    Private ReadOnly Property ScanFindsMemory As Boolean
        Get
            Return String.Equals(_currentSystem, "VIC20", StringComparison.OrdinalIgnoreCase) OrElse IsC16System
        End Get
    End Property

    ' On the C16 list the column says which machine a game needs: C16 (16K) or Plus/4
    Private ReadOnly Property IsC16System As Boolean
        Get
            Return String.Equals(_currentSystem, "C16", StringComparison.OrdinalIgnoreCase)
        End Get
    End Property

    ' 16K means a C16; 32K or 64K a Plus/4. Machine names (from an earlier version or
    ' typed in) turn back into sizes: C16 is 16K, Plus/4 64K.
    Private Shared Function MachineName(memory As String) As String
        Dim plain As String = memory.Replace("?", "").Trim().ToUpperInvariant()
        Dim unsure As String = If(memory.Contains("?"), "?", "")
        Select Case plain
            Case "16K", "C16", "C116" : Return "C16" & unsure
            Case "32K", "64K", "PLUS/4", "PLUS4", "+4" : Return "Plus/4" & unsure
        End Select
        Return ""
    End Function

    Private Shared Function C16RamName(memory As String) As String
        Dim plain As String = memory.Replace("?", "").Trim().ToUpperInvariant()
        Dim unsure As String = If(memory.Contains("?"), "?", "")
        Select Case plain
            Case "C16", "C116" : Return "16K" & unsure
            Case "PLUS/4", "PLUS4", "+4" : Return "64K" & unsure
        End Select
        Return memory
    End Function

    ' C16 list only: which machine a game needs, from its RAM
    Private Function GameMachine(label As String) As String
        Return MachineName(MemoryFor(label))
    End Function

    ' The RAM a game needs: from its info file, else from the background scan. Never touches the disk.
    Private Function MemoryFor(label As String) As String
        Dim details As GameDetails = Nothing
        Dim fromInfo As String = If(_gameDetails.TryGetValue(label, details), details.Memory, "")
        Dim scanned As String = ""
        If ScanFindsMemory Then
            Dim gameFile As String = Nothing
            Dim memory As String = Nothing
            If _gameFiles.TryGetValue(label, gameFile) AndAlso _fileStamps.ContainsKey(gameFile) AndAlso
               _tapeMemory.TryGetValue(FastCheckKey(gameFile), memory) Then scanned = memory
        End If
        If IsC16System Then
            ' The game file itself is the better guide here: listings call some Plus/4
            ' versions 16K. Its value is used unless the scan could not be sure.
            If scanned <> "" AndAlso Not scanned.Contains("?") Then Return C16RamName(scanned)
            If fromInfo <> "" Then Return C16RamName(fromInfo)
            Return C16RamName(scanned)
        End If
        Return If(fromInfo <> "", fromInfo, scanned)
    End Function

    ' Brackets are hidden, except ones that tell parts apart, such as (Disk 1) or (Side B)
    Private Shared ReadOnly _keepBrackets As New System.Text.RegularExpressions.Regex(
        "\b(dis[ck]|side|part|tape|cass(ette)?|file|load|level|cheat|cheats|trainer)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)

    ' Which side, tape or part a file is, and whether it has a cheat: kept in the list
    ' name, each in its own brackets, so the parts show in order and are not taken for
    ' duplicates. The same patterns as cbmassets.py, which leaves them out of searches.
    Private Const PART_MARK As String = "(?:side|tape|part|dis[ck]|cass(?:ette)?|file|load)s?[\s_.-]*(?:\d+|[a-z])(?![a-z0-9])" &
        "(?:\s*(?:&|and|\+|-|to)\s*(?:\d+|[a-z])(?![a-z0-9]))*(?:\s*of\s*\d+)?"
    Private Const CHEAT_MARK As String = "(?:cheat(?:s|ed)?(?:\s*version)?|trainer)(?![a-z0-9])"
    Private Shared ReadOnly TITLE_MARK As String = $"(?:{PART_MARK}|{CHEAT_MARK})"
    Private Const MarkOptions As System.Text.RegularExpressions.RegexOptions = System.Text.RegularExpressions.RegexOptions.IgnoreCase

    ' "side_a" becomes "Side A", "tape1 of 2" "Tape 1 of 2", "cheats" "Cheat"
    Private Shared Function TidyMarker(mark As String) As String
        mark = System.Text.RegularExpressions.Regex.Replace(mark.Trim(), "[\s_.]+", " ")
        If System.Text.RegularExpressions.Regex.IsMatch(mark, "^(cheat|trainer)", MarkOptions) Then
            Return If(mark.StartsWith("trainer", StringComparison.OrdinalIgnoreCase), "Trainer", "Cheat")
        End If
        Dim found = System.Text.RegularExpressions.Regex.Match(mark,
            "^([a-z]+?)s?\s*-?\s*(\d+|[a-z])((?:\s*(?:&|and|\+|-|to)\s*(?:\d+|[a-z]))*)(?:\s*of\s*(\d+))?$", MarkOptions)
        If Not found.Success Then Return Char.ToUpperInvariant(mark(0)) & mark.Substring(1)
        Dim word As String = found.Groups(1).Value.ToLowerInvariant()
        Select Case word
            Case "disc" : word = "Disk"
            Case "cas", "cass", "cassette" : word = "Cassette"
            Case Else : word = Char.ToUpperInvariant(word(0)) & word.Substring(1)
        End Select
        Dim more As String = System.Text.RegularExpressions.Regex.Replace(found.Groups(3).Value, "\s*(&|and|\+|-|to)\s*",
            Function(m) If(m.Groups(1).Value = "-" OrElse m.Groups(1).Value.ToLowerInvariant() = "to", "-", " & "), MarkOptions).ToUpperInvariant()
        Return $"{word} {found.Groups(2).Value.ToUpperInvariant()}{more}" &
            If(found.Groups(4).Success, $" of {found.Groups(4).Value}", "")
    End Function

    ' ("Elite", {"Tape 1", "Side A"}) from "Elite - Tape 1 Side A"; a title that is only a mark stays whole
    Private Shared Function SplitTitleMarks(title As String) As (Core As String, Marks As List(Of String))
        Dim marks As New List(Of String)
        Dim core As String = title
        Do
            Dim found = System.Text.RegularExpressions.Regex.Match(core, $"(?:^|[\s_,:;.-]+)({TITLE_MARK})[\s_,:;.-]*$", MarkOptions)
            If Not found.Success OrElse core.Substring(0, found.Index).Trim(" "c, "_"c, "-"c, ","c, ":"c, ";"c, "."c) = "" Then Exit Do
            marks.Insert(0, TidyMarker(found.Groups(1).Value))
            core = core.Substring(0, found.Index)
        Loop
        core = core.TrimEnd(" "c, "_"c, "-"c, ","c, ":"c, ";"c, "."c)
        If core = "" Then Return (title, New List(Of String))
        Return (core, marks)
    End Function

    ' Is a bracket's content nothing but marks, such as "Tape 1, Side A" or "Cheat"?
    Private Shared Function MarksInBracket(content As String) As List(Of String)
        If Not System.Text.RegularExpressions.Regex.IsMatch(content, $"^\s*{TITLE_MARK}(?:[\s,;&+]+{TITLE_MARK})*\s*$", MarkOptions) Then Return Nothing
        Return System.Text.RegularExpressions.Regex.Matches(content, TITLE_MARK, MarkOptions).
            Cast(Of System.Text.RegularExpressions.Match)().Select(Function(m) TidyMarker(m.Value)).ToList()
    End Function

    ' Every side, tape, part and cheat mark in a name, tidied, in order
    Private Shared Function TitleMarks(name As String) As List(Of String)
        Dim head As String = System.Text.RegularExpressions.Regex.Split(name, "\s*[(\[]")(0).Trim()
        Dim marks As New List(Of String)(SplitTitleMarks(head).Marks)
        For Each group As System.Text.RegularExpressions.Match In System.Text.RegularExpressions.Regex.Matches(name, "[(\[]([^)\]]*)[)\]]")
            Dim inBracket = MarksInBracket(group.Groups(1).Value)
            If inBracket IsNot Nothing Then marks.AddRange(inBracket)
        Next
        Return marks.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    ' Words some collections add to say which machine a file is for
    Private Shared ReadOnly _machineWords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "c16", "c116", "c264", "plus4", "p4", "c16plus4", "vic20", "c64"}
    Private Shared ReadOnly _smallWords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "a", "an", "and", "the", "of", "at", "in", "on", "to", "for", "or", "by",
        "vs", "with", "from", "into", "but", "nor", "as", "de"}

    ' Words without a machine name at the start or end, such as "c16" or "plus 4"
    Private Shared Function DropMachineWords(words As List(Of String)) As List(Of String)
        Dim kept As New List(Of String)(words)
        While kept.Count > 1
            Dim lastTwo As String = If(kept.Count > 2, (kept(kept.Count - 2) & " " & kept(kept.Count - 1)).ToLowerInvariant(), "")
            If _machineWords.Contains(kept(kept.Count - 1)) Then
                kept.RemoveAt(kept.Count - 1)
            ElseIf lastTwo = "plus 4" OrElse lastTwo = "c16 4" Then
                kept.RemoveRange(kept.Count - 2, 2)
            ElseIf _machineWords.Contains(kept(0)) Then
                kept.RemoveAt(0)
            Else
                Exit While
            End If
        End While
        Return kept
    End Function

    ' "jet_set_willy_c16" becomes "Jet Set Willy". A name without underscores that is
    ' not all lower case is left as it is, apart from a machine name on the end.
    Private Shared Function TidyTitle(name As String) As String
        Dim title As String = System.Text.RegularExpressions.Regex.Split(name, "\s*[(\[]")(0).Trim()
        Dim rest As String = If(name.StartsWith(title), name.Substring(title.Length), "").Replace("_", " ").Trim()
        Dim spaced As String = System.Text.RegularExpressions.Regex.Replace(title.Replace("_", " "), "\s+", " ").Trim()
        Dim words As List(Of String) = DropMachineWords(spaced.Split({" "c}, StringSplitOptions.RemoveEmptyEntries).ToList())
        If title.Contains("_") OrElse (title = title.ToLowerInvariant() AndAlso title.Any(Function(c) Char.IsLetter(c))) Then
            Dim out As New List(Of String)
            For Each word In words
                Dim lower As String = word.ToLowerInvariant()
                Dim startsPart As Boolean = out.Count = 0 OrElse out(out.Count - 1) = "-" OrElse out(out.Count - 1).EndsWith(":")
                If _smallWords.Contains(lower) AndAlso Not startsPart Then
                    out.Add(lower)
                ElseIf lower = "ii" OrElse lower = "iii" OrElse lower = "iv" OrElse
                       System.Text.RegularExpressions.Regex.IsMatch(lower, "^\d+d$") Then
                    out.Add(word.ToUpperInvariant())
                Else
                    out.Add(Char.ToUpperInvariant(word(0)) & word.Substring(1))
                End If
            Next
            words = out
        End If
        Dim tidied As String = String.Join(" ", words) & If(rest = "", "", " " & rest)
        Return If(tidied.Trim() = "", name, tidied.Trim())
    End Function

    ' The list name: the tidied title, then each side, tape, part or cheat mark in
    ' brackets (wherever it was in the file name), then any other bracket worth keeping
    Private Shared Function CleanTitle(name As String) As String
        name = TidyTitle(name)
        Dim head As String = System.Text.RegularExpressions.Regex.Split(name, "\s*[(\[]")(0).Trim()
        Dim rest As String = If(name.StartsWith(head), name.Substring(head.Length), "")
        Dim split = SplitTitleMarks(head)
        Dim marks As New List(Of String)(split.Marks)
        Dim others As New List(Of String)
        For Each group As System.Text.RegularExpressions.Match In System.Text.RegularExpressions.Regex.Matches(rest, "[(\[]([^)\]]*)[)\]]")
            Dim content As String = group.Groups(1).Value.Trim()
            Dim inBracket = MarksInBracket(content)
            If inBracket IsNot Nothing Then
                marks.AddRange(inBracket)
            ElseIf _keepBrackets.IsMatch(content) Then
                others.Add(content)
            End If
        Next
        Dim cleaned As String = split.Core &
            String.Concat(marks.Distinct(StringComparer.OrdinalIgnoreCase).Select(Function(mark) $" ({mark})")) &
            String.Concat(others.Select(Function(other) $" ({other})"))
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "\s{2,}", " ").Trim()
        Return If(cleaned = "", name, cleaned)
    End Function

    ' The list name: the cleaned title, plus any tag added to tell same-named games apart
    Private Function DisplayName(label As String) As String
        Dim known As String = Nothing
        If _displayNames.TryGetValue(label, known) Then Return known
        Dim gameFile As String = Nothing
        Dim plainName As String = If(_gameFiles.TryGetValue(label, gameFile), Path.GetFileNameWithoutExtension(gameFile), label)
        Dim tag As String = If(label.StartsWith(plainName, StringComparison.OrdinalIgnoreCase), label.Substring(plainName.Length), "")
        Return CleanTitle(plainName) & tag
    End Function

    Private Function DetailsFor(label As String) As GameDetails
        Dim details As GameDetails = Nothing
        If _gameDetails.TryGetValue(label, details) Then Return details
        Return New GameDetails()
    End Function

    ' Year, publisher and genre from the game's info file, with the year and publisher
    ' from a name such as "Game (1984)(Ocean)" when the info file does not say
    Private Sub LoadGameDetails()
        _knownGenres = Nothing
        _gameDetails.Clear()
        _displayNames.Clear()
        _searchTexts.Clear()
        LoadTitleOverrides()

        ' One listing of the info folder instead of a check per game
        Dim infoFiles As New Dictionary(Of String, FileInfo)(StringComparer.OrdinalIgnoreCase)
        Try
            If Directory.Exists(InfoFolder()) Then
                For Each info In New DirectoryInfo(InfoFolder()).EnumerateFiles("*.txt")
                    infoFiles(Path.GetFileNameWithoutExtension(info.Name)) = info
                Next
            End If
        Catch ex As IOException
            Debug.WriteLine($"Could not list {InfoFolder()}: {ex.Message}")
        End Try

        For Each entry In _gameFiles
            Dim plainName As String = Path.GetFileNameWithoutExtension(entry.Value)
            Dim details As New GameDetails()
            Dim infoFile As FileInfo = Nothing
            If infoFiles.TryGetValue(entry.Key, infoFile) OrElse infoFiles.TryGetValue(plainName, infoFile) Then
                Dim fromInfo As GameDetails = ReadInfoDetails(infoFile)
                details.Year = fromInfo.Year
                details.Publisher = fromInfo.Publisher
                details.Genre = fromInfo.Genre
                details.Memory = fromInfo.Memory
            End If

            Dim tosec = System.Text.RegularExpressions.Regex.Match(plainName, "\((19\d\d|20\d\d)[^)]*\)(?:\(([^)]+)\))?")
            If tosec.Success Then
                If details.Year = "" Then details.Year = tosec.Groups(1).Value
                If details.Publisher = "" AndAlso tosec.Groups(2).Success AndAlso tosec.Groups(2).Value <> "-" Then
                    details.Publisher = tosec.Groups(2).Value
                End If
            End If
            _gameDetails(entry.Key) = details

            Dim tag As String = If(entry.Key.StartsWith(plainName, StringComparison.OrdinalIgnoreCase), entry.Key.Substring(plainName.Length), "")
            Dim shownName As String = Nothing
            If Not _titleOverrides.TryGetValue(entry.Key, shownName) Then shownName = CleanTitle(plainName) & tag
            _displayNames(entry.Key) = shownName
            _searchTexts(entry.Key) = String.Join(" ", {entry.Key, shownName, details.Year, details.Publisher,
                                                         String.Join(" ", GenresOf(details))}).ToLowerInvariant()
        Next
        SaveInfoCache()
    End Sub

    Private Function ReadInfoDetails(infoFile As FileInfo) As GameDetails
        Dim infoPath As String = infoFile.FullName
        Dim stamp As Long = infoFile.LastWriteTimeUtc.Ticks
        Dim cached As KeyValuePair(Of Long, GameDetails) = Nothing
        If _infoCache.TryGetValue(infoPath, cached) AndAlso cached.Key = stamp Then Return cached.Value

        Dim details As New GameDetails()
        Dim developer As String = ""
        Try
            For Each line In File.ReadLines(infoPath)
                If line.StartsWith("About:") Then Exit For
                Dim colon As Integer = line.IndexOf(":"c)
                If colon < 0 Then Continue For
                Dim value As String = line.Substring(colon + 1).Trim()
                Select Case line.Substring(0, colon).Trim().ToLowerInvariant()
                    Case "year" : If details.Year = "" Then details.Year = value
                    Case "publisher" : If details.Publisher = "" Then details.Publisher = value
                    Case "developer" : If developer = "" Then developer = value
                    Case "genre" : If details.Genre = "" Then details.Genre = value
                    Case "memory" : If details.Memory = "" Then details.Memory = value
                End Select
            Next
        Catch ex As IOException
            Debug.WriteLine($"Could not read {infoPath}: {ex.Message}")
        End Try
        If details.Publisher = "" Then details.Publisher = developer
        _infoCache(infoPath) = New KeyValuePair(Of Long, GameDetails)(stamp, details)
        _infoCacheChanged = True
        Return details
    End Function

    Private Shared Function GenresOf(details As GameDetails) As IEnumerable(Of String)
        Return details.Genre.Split({","c, "/"c, ";"c}, StringSplitOptions.RemoveEmptyEntries).
            Select(Function(g) CanonicalGenre(g)).Where(Function(g) g <> "").
            Distinct(StringComparer.OrdinalIgnoreCase)
    End Function

    Private Sub SetUpGameList()
        ' Column headings sit just above the list, which moves down to make room
        _listHeader = New Panel() With {.Height = HEADER_HEIGHT, .BackColor = SystemColors.Control}
        AddHandler _listHeader.Paint, AddressOf PaintListHeader
        ListBox1.Top += HEADER_HEIGHT
        ListBox1.Height -= HEADER_HEIGHT
        _listHeader.Bounds = New Rectangle(ListBox1.Left, ListBox1.Top - HEADER_HEIGHT, ListBox1.Width, HEADER_HEIGHT)
        Me.Controls.Add(_listHeader)

        ListBox1.DrawMode = DrawMode.OwnerDrawFixed
        ListBox1.ItemHeight = ListBox1.Font.Height + 4
        AddHandler ListBox1.DrawItem, AddressOf DrawGameRow
        AddHandler ListBox1.Resize, Sub(sender, e)
                                        ListBox1.Invalidate()
                                        _listHeader.Invalidate()
                                    End Sub

        ' The view choice, and on the VIC-20 and C16 the RAM choice, sit at the
        ' right-hand end of the search box
        _filterRightGap = ListBox1.Right - SearchBox.Right
        _viewCombo = New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 120,
                                          .DrawMode = DrawMode.OwnerDrawFixed, .DropDownHeight = 400}
        _viewCombo.Items.AddRange({VIEW_ALL, VIEW_FAVOURITES})
        _viewCombo.SelectedIndex = 0
        AddHandler _viewCombo.DrawItem, AddressOf DrawViewChoice
        AddHandler _viewCombo.SelectedIndexChanged, Sub(sender, e)
                                                        If _updatingView Then Return
                                                        ' A heading cannot be chosen: carry on past it the way the user was going
                                                        If TypeOf _viewCombo.SelectedItem Is ViewHeading Then
                                                            Dim stepBy As Integer = If(_viewCombo.SelectedIndex >= _lastViewIndex, 1, -1)
                                                            Dim index As Integer = _viewCombo.SelectedIndex
                                                            Do
                                                                index += stepBy
                                                            Loop While index >= 0 AndAlso index < _viewCombo.Items.Count AndAlso TypeOf _viewCombo.Items(index) Is ViewHeading
                                                            If index < 0 OrElse index >= _viewCombo.Items.Count Then index = _lastViewIndex
                                                            _viewCombo.SelectedIndex = index
                                                            Return
                                                        End If
                                                        _lastViewIndex = _viewCombo.SelectedIndex
                                                        ApplyGameFilter()
                                                    End Sub

        _ramCombo = New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 80}
        _ramCombo.Items.Add(RAM_ALL)
        _ramCombo.SelectedIndex = 0
        ' The choices are those the games have now; the scan fills them in as it goes
        AddHandler _ramCombo.DropDown, Sub(sender, e) RefreshRamChoices()
        AddHandler _ramCombo.SelectedIndexChanged, Sub(sender, e)
                                                       If Not _updatingView Then ApplyGameFilter()
                                                   End Sub
        Dim tips As New ToolTip()
        tips.SetToolTip(_ramCombo, "Show only games that need this much RAM")

        ' C16 list only: C16 or Plus/4
        _machineCombo = New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 90}
        _machineCombo.Items.AddRange({MACHINE_ALL, "C16", "Plus/4", RAM_UNKNOWN})
        _machineCombo.SelectedIndex = 0
        AddHandler _machineCombo.SelectedIndexChanged, Sub(sender, e)
                                                           If Not _updatingView Then ApplyGameFilter()
                                                       End Sub
        tips.SetToolTip(_machineCombo, "Show only games for this machine")

        Me.Controls.Add(_viewCombo)
        Me.Controls.Add(_ramCombo)
        Me.Controls.Add(_machineCombo)
        PlaceFilterControls()
        ' Controls added in code sit behind the designer's, so bring these forward
        _viewCombo.BringToFront()
        _ramCombo.BringToFront()
        _machineCombo.BringToFront()
        _listHeader.BringToFront()
    End Sub

    ' The view box, then the RAM box when this system shows RAM, at the end of the search box
    Private Sub PlaceFilterControls()
        If _viewCombo Is Nothing Then Return
        Dim rightEdge As Integer = ListBox1.Right - _filterRightGap
        Dim showRam As Boolean = ShowMemoryColumn     ' (Visible reads False until the window is shown)
        Dim showMachine As Boolean = IsC16System
        _machineCombo.Visible = showMachine
        If showMachine Then
            _machineCombo.Location = New Point(rightEdge - _machineCombo.Width, SearchBox.Top)
            rightEdge = _machineCombo.Left - 6
        ElseIf _machineCombo.SelectedIndex > 0 Then
            _updatingView = True
            _machineCombo.SelectedIndex = 0
            _updatingView = False
        End If
        _ramCombo.Visible = showRam
        If showRam Then
            _ramCombo.Location = New Point(rightEdge - _ramCombo.Width, SearchBox.Top)
            rightEdge = _ramCombo.Left - 6
        ElseIf _ramCombo.SelectedIndex > 0 Then
            _updatingView = True
            _ramCombo.SelectedIndex = 0
            _updatingView = False
        End If
        _viewCombo.Location = New Point(rightEdge - _viewCombo.Width, SearchBox.Top)
        SearchBox.Width = Math.Max(60, _viewCombo.Left - 6 - SearchBox.Left)
    End Sub

    Private Sub DrawViewChoice(sender As Object, e As DrawItemEventArgs)
        If e.Index < 0 OrElse _viewCombo Is Nothing Then Return
        Dim choice As Object = _viewCombo.Items(e.Index)
        Dim flags As TextFormatFlags = TextFormatFlags.VerticalCenter Or TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix
        If TypeOf choice Is ViewHeading Then
            e.Graphics.FillRectangle(SystemBrushes.Window, e.Bounds)
            e.Graphics.DrawLine(SystemPens.ControlDark, e.Bounds.Left + 2, e.Bounds.Top + 1, e.Bounds.Right - 3, e.Bounds.Top + 1)
            Using bold As New Font(e.Font, FontStyle.Bold)
                TextRenderer.DrawText(e.Graphics, choice.ToString(), bold, New Rectangle(e.Bounds.Left + 2, e.Bounds.Top + 2, e.Bounds.Width - 4, e.Bounds.Height - 2),
                                      SystemColors.GrayText, flags)
            End Using
            Return
        End If
        e.DrawBackground()
        Dim inBox As Boolean = (e.State And DrawItemState.ComboBoxEdit) = DrawItemState.ComboBoxEdit
        Dim indent As Integer = If(TypeOf choice Is GenreChoice AndAlso Not inBox, 10, 0)
        Dim selected As Boolean = (e.State And DrawItemState.Selected) = DrawItemState.Selected
        TextRenderer.DrawText(e.Graphics, choice.ToString(), e.Font, New Rectangle(e.Bounds.Left + 2 + indent, e.Bounds.Top, e.Bounds.Width - 4 - indent, e.Bounds.Height),
                              If(selected, SystemColors.HighlightText, SystemColors.WindowText), flags)
        e.DrawFocusRectangle()
    End Sub

    ' The RAM column: what is known, … while the scan has still to reach the game,
    ' and ? if the scan could not tell (or could not read the file)
    Private Function RamCellFor(label As String) As String
        Dim memory As String = MemoryFor(label)
        If memory <> "" OrElse Not ScanFindsMemory Then Return memory
        Dim gameFile As String = Nothing
        If Not _gameFiles.TryGetValue(label, gameFile) OrElse Not _fileStamps.ContainsKey(gameFile) Then Return NOT_CHECKED
        Dim key As String = FastCheckKey(gameFile)
        If _fastCheckFailures.ContainsKey(key) Then Return "?"
        Return If(_fastCheckCache.ContainsKey(key), "?", NOT_CHECKED)
    End Function

    Private Const NOT_CHECKED As String = "…"

    ' While the scan runs with a RAM size picked, games move to their own size as
    ' they are checked. Done every couple of seconds, not for every game.
    Private _ramRefilterTimer As Timer = Nothing

    Private Sub RefilterForRamSoon()
        If _ramCombo Is Nothing OrElse _ramCombo.SelectedIndex <= 0 OrElse Not ShowMemoryColumn Then Return
        If _ramRefilterTimer Is Nothing Then
            _ramRefilterTimer = New Timer() With {.Interval = 2000}
            AddHandler _ramRefilterTimer.Tick, Sub(sender, e)
                                                   _ramRefilterTimer.Stop()
                                                   If IsTapePlaying() Then Return       ' the list is locked while a tape plays
                                                   Dim top As Integer = ListBox1.TopIndex
                                                   ApplyGameFilterKeeping(SelectedGames())
                                                   If top < ListBox1.Items.Count Then ListBox1.TopIndex = top
                                               End Sub
        End If
        If Not _ramRefilterTimer.Enabled Then _ramRefilterTimer.Start()
    End Sub

    ' What a game's RAM counts as in the RAM box: None, 3K, 16K, A000 and so on
    Private Function RamChoiceFor(label As String) As String
        Dim ram As String = MemoryFor(label).Replace("?", "").Trim()
        If ram = "" Then Return RAM_UNKNOWN
        Dim first As String = ram.Split(" "c)(0)
        Return If(first.Equals("none", StringComparison.OrdinalIgnoreCase), "None", first.ToUpperInvariant())
    End Function

    Private Sub RefreshRamChoices()
        If _ramCombo Is Nothing Then Return
        Dim current As String = If(_ramCombo.SelectedItem?.ToString(), RAM_ALL)
        Dim sizes = _allGames.Select(Function(game) RamChoiceFor(game)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Dim ordered = sizes.OrderBy(Function(sizeName) RamOrder(sizeName)).ThenBy(Function(sizeName) sizeName).ToList()
        _updatingView = True
        _ramCombo.BeginUpdate()
        _ramCombo.Items.Clear()
        _ramCombo.Items.Add(RAM_ALL)
        For Each sizeName In ordered
            _ramCombo.Items.Add(sizeName)
        Next
        Dim index As Integer = _ramCombo.Items.IndexOf(current)
        _ramCombo.SelectedIndex = If(index >= 0, index, 0)
        _ramCombo.EndUpdate()
        _updatingView = False
    End Sub

    ' None first, then sizes by number, then anything else, then Unknown
    Private Shared Function RamOrder(sizeName As String) As Integer
        If sizeName = "None" Then Return 0
        If sizeName = RAM_UNKNOWN Then Return 100000
        Dim number As Integer
        If sizeName.EndsWith("K") AndAlso Integer.TryParse(sizeName.TrimEnd("K"c), number) Then Return number
        Return 50000
    End Function

    ' Title, (RAM on the VIC-20,) Year, Publisher and Genre column edges for a list of a given width
    Private Const MACHINE_COLUMN_WIDTH As Integer = 62

    ' Columns between Title and Year: RAM on the VIC-20; RAM and Machine on the C16
    Private Function ExtraColumnWidths() As Integer()
        If IsC16System Then Return {RAM_COLUMN_WIDTH, MACHINE_COLUMN_WIDTH}
        If ShowMemoryColumn Then Return {RAM_COLUMN_WIDTH}
        Return {}
    End Function

    Private Function ColumnEdges(width As Integer) As Integer()
        Dim extras = ExtraColumnWidths()
        Dim extraWidth As Integer = extras.Sum()
        Dim rest As Integer = Math.Max(width - YEAR_COLUMN_WIDTH - extraWidth - 8, 100)
        Dim titleWidth As Integer = CInt(rest * 0.55)
        Dim publisherWidth As Integer = CInt(rest * 0.25)
        Dim edges As New List(Of Integer) From {0, titleWidth}
        Dim position As Integer = titleWidth
        For Each extra In extras
            position += extra
            edges.Add(position)
        Next
        Dim yearEnd As Integer = position + YEAR_COLUMN_WIDTH
        edges.AddRange({yearEnd, yearEnd + publisherWidth, width})
        Return edges.ToArray()
    End Function

    Private Function ColumnHeadings() As String()
        If IsC16System Then Return {"Title", "RAM", "Machine", "Year", "Publisher", "Genre"}
        If ShowMemoryColumn Then Return {"Title", "RAM", "Year", "Publisher", "Genre"}
        Return {"Title", "Year", "Publisher", "Genre"}
    End Function

    Private Sub PaintListHeader(sender As Object, e As PaintEventArgs)
        Dim edges = ColumnEdges(ListBox1.ClientSize.Width)
        Dim headings As String() = ColumnHeadings()
        Using boldFont As New Font(ListBox1.Font, FontStyle.Bold)
            For index As Integer = 0 To headings.Length - 1
                Dim cell As New Rectangle(edges(index) + 3, 0, edges(index + 1) - edges(index) - 4, HEADER_HEIGHT)
                TextRenderer.DrawText(e.Graphics, headings(index), boldFont, cell, SystemColors.ControlText,
                                      TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            Next
        End Using
    End Sub

    Private Sub DrawGameRow(sender As Object, e As DrawItemEventArgs)
        If e.Index < 0 OrElse e.Index >= ListBox1.Items.Count Then Return
        e.DrawBackground()
        Dim label As String = ListBox1.Items(e.Index).ToString()
        Dim details As GameDetails = DetailsFor(label)
        Dim selected As Boolean = (e.State And DrawItemState.Selected) = DrawItemState.Selected
        Dim textColor As Color = If(selected, SystemColors.HighlightText, ListBox1.ForeColor)
        Dim edges = ColumnEdges(e.Bounds.Width)
        Dim rowName As String = If(_favourites.Contains(label), "★ ", "") & DisplayName(label)
        Dim cells As New List(Of String) From {rowName, details.Year, details.Publisher, String.Join(", ", GenresOf(details))}
        If ShowMemoryColumn Then cells.Insert(1, RamCellFor(label))
        If IsC16System Then cells.Insert(2, GameMachine(label))
        For index As Integer = 0 To cells.Count - 1
            Dim cell As New Rectangle(e.Bounds.Left + edges(index) + 3, e.Bounds.Top, edges(index + 1) - edges(index) - 4, e.Bounds.Height)
            TextRenderer.DrawText(e.Graphics, cells(index), e.Font, cell, textColor,
                                  TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix)
        Next
        e.DrawFocusRectangle()
    End Sub

    ' Rebuilds the view list: All Games, Favourites, then each genre found
    Private Sub RefreshViewChoices()
        If _viewCombo Is Nothing Then Return
        Dim current As String = If(_viewCombo.SelectedItem?.ToString(), VIEW_ALL)
        Dim genres = _gameDetails.Values.SelectMany(Function(d) GenresOf(d)).
            Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Function(g) g, StringComparer.OrdinalIgnoreCase).ToList()

        Dim currentIsGenre As Boolean = TypeOf _viewCombo.SelectedItem Is GenreChoice

        _updatingView = True
        _viewCombo.BeginUpdate()
        _viewCombo.Items.Clear()
        _viewCombo.Items.Add(VIEW_ALL)
        _viewCombo.Items.Add(VIEW_FAVOURITES)
        If genres.Count > 0 Then _viewCombo.Items.Add(New ViewHeading("Genres"))
        For Each genre In genres
            _viewCombo.Items.Add(New GenreChoice(genre))
        Next
        Dim index As Integer = 0
        For position As Integer = 0 To _viewCombo.Items.Count - 1
            Dim item As Object = _viewCombo.Items(position)
            If TypeOf item Is ViewHeading Then Continue For
            If (TypeOf item Is GenreChoice) = currentIsGenre AndAlso item.ToString() = current Then
                index = position
                Exit For
            End If
        Next
        _viewCombo.SelectedIndex = index
        _lastViewIndex = index
        _viewCombo.EndUpdate()
        _updatingView = False
    End Sub

    ' True when a game belongs in the chosen view and RAM size
    Private Function InCurrentView(label As String) As Boolean
        If _machineCombo IsNot Nothing AndAlso IsC16System AndAlso _machineCombo.SelectedIndex > 0 Then
            Dim machine As String = GameMachine(label).Replace("?", "")
            If machine = "" Then machine = RAM_UNKNOWN
            If Not String.Equals(machine, _machineCombo.SelectedItem.ToString(), StringComparison.OrdinalIgnoreCase) Then Return False
        End If
        If _ramCombo IsNot Nothing AndAlso ShowMemoryColumn AndAlso _ramCombo.SelectedIndex > 0 AndAlso
           Not String.Equals(RamChoiceFor(label), _ramCombo.SelectedItem.ToString(), StringComparison.OrdinalIgnoreCase) Then
            Return False
        End If
        Dim choice As Object = _viewCombo?.SelectedItem
        Dim genre = TryCast(choice, GenreChoice)
        If genre IsNot Nothing Then Return GenresOf(DetailsFor(label)).Contains(genre.Name, StringComparer.OrdinalIgnoreCase)
        If choice IsNot Nothing AndAlso choice.ToString() = VIEW_FAVOURITES Then Return _favourites.Contains(label)
        Return True
    End Function

    ' Searching looks at the shown name, year, publisher and genre
    Private Function SearchTextFor(label As String) As String
        Dim known As String = Nothing
        If _searchTexts.TryGetValue(label, known) Then
            If ShowMemoryColumn Then Return known & " " & MemoryFor(label).ToLowerInvariant()
            Return known
        End If
        Dim details As GameDetails = DetailsFor(label)
        Return String.Join(" ", {label, DisplayName(label), details.Year, details.Publisher, details.Genre}).ToLowerInvariant()
    End Function

    Private Sub SetFavourite(label As String, isFavourite As Boolean)
        If isFavourite Then _favourites.Add(label) Else _favourites.Remove(label)
        SaveFastLoadSettings()
        If _viewCombo IsNot Nothing AndAlso TypeOf _viewCombo.SelectedItem Is String AndAlso _viewCombo.SelectedItem.ToString() = VIEW_FAVOURITES Then
            ApplyGameFilter()
        Else
            ListBox1.Invalidate()
        End If
    End Sub

    ' Start-up speed: what earlier runs worked out is saved in the system's Settings
    ' folder, so a start only reads files that are new or have changed

    Private ReadOnly _fileStamps As New Dictionary(Of String, KeyValuePair(Of Long, Long))(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _displayNames As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _searchTexts As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private _fingerprintsChanged As Boolean = False
    Private _infoCacheChanged As Boolean = False

    Private ReadOnly Property _fingerprintCachePath As String
        Get
            Return Path.Combine(SystemSettingsFolder(_currentSystem), "fingerprints.txt")
        End Get
    End Property

    Private ReadOnly Property _infoCachePath As String
        Get
            Return Path.Combine(SystemSettingsFolder(_currentSystem), "details_cache.txt")
        End Get
    End Property

    ' Size and date of a game file, from the folder listing when there is one
    Private Function FileStamp(gameFile As String) As KeyValuePair(Of Long, Long)
        Dim stamp As KeyValuePair(Of Long, Long)
        If _fileStamps.TryGetValue(gameFile, stamp) Then Return stamp
        Dim info As New FileInfo(gameFile)
        stamp = New KeyValuePair(Of Long, Long)(info.Length, info.LastWriteTimeUtc.Ticks)
        _fileStamps(gameFile) = stamp
        Return stamp
    End Function

    Private Sub LoadStartupCaches()
        _fingerprints.Clear()
        _infoCache.Clear()
        _fingerprintsChanged = False
        _infoCacheChanged = False
        Try
            If File.Exists(_fingerprintCachePath) Then
                For Each line In File.ReadLines(_fingerprintCachePath)
                    Dim parts = line.Split(vbTab(0))
                    If parts.Length = 2 Then _fingerprints(parts(0)) = parts(1)
                Next
            End If
            If File.Exists(_infoCachePath) Then
                For Each line In File.ReadLines(_infoCachePath)
                    Dim parts = line.Split(vbTab(0))
                    Dim ticks As Long
                    If parts.Length >= 5 AndAlso Long.TryParse(parts(1), ticks) Then
                        _infoCache(parts(0)) = New KeyValuePair(Of Long, GameDetails)(ticks,
                            New GameDetails() With {.Year = parts(2), .Publisher = parts(3), .Genre = parts(4),
                                                    .Memory = If(parts.Length >= 6, parts(5), "")})
                    End If
                Next
            End If
        Catch ex As IOException
            Debug.WriteLine($"Could not read start-up caches: {ex.Message}")
        End Try
    End Sub

    Private Sub SaveFingerprintCache()
        If Not _fingerprintsChanged Then Return
        Try
            ' Only entries for games that are still here
            Dim present = _gameFiles.Values.ToHashSet(StringComparer.OrdinalIgnoreCase)
            File.WriteAllLines(_fingerprintCachePath, _fingerprints.
                Where(Function(entry) present.Contains(entry.Key.Substring(0, Math.Max(0, entry.Key.LastIndexOf("|"c, entry.Key.LastIndexOf("|"c) - 1))))).
                Select(Function(entry) entry.Key & vbTab & entry.Value))
            _fingerprintsChanged = False
        Catch ex As IOException
            Debug.WriteLine($"Could not save fingerprints: {ex.Message}")
        End Try
    End Sub

    Private Sub SaveInfoCache()
        If Not _infoCacheChanged Then Return
        Try
            File.WriteAllLines(_infoCachePath, _infoCache.Select(
                Function(entry) String.Join(vbTab, {entry.Key, entry.Value.Key.ToString(),
                                                    Clean(entry.Value.Value.Year), Clean(entry.Value.Value.Publisher), Clean(entry.Value.Value.Genre),
                                                    Clean(entry.Value.Value.Memory)})))
            _infoCacheChanged = False
        Catch ex As IOException
            Debug.WriteLine($"Could not save the details cache: {ex.Message}")
        End Try
    End Sub

    Private Shared Function Clean(value As String) As String
        Return If(value, "").Replace(vbTab, " ").Replace(vbCr, " ").Replace(vbLf, " ")
    End Function

    ' Genres: spellings of the same genre, such as "shoot 'em up", "Shoot-'em-up" and
    ' "shoot_em_up", are shown and grouped as one

    Private Shared ReadOnly _genreNames As New Dictionary(Of String, String) From {
        {"shootemup", "Shoot 'em up"}, {"shootthemup", "Shoot 'em up"}, {"shmup", "Shoot 'em up"},
        {"shooter", "Shoot 'em up"}, {"scrollingshooter", "Shoot 'em up"}, {"fixedshooter", "Shoot 'em up"},
        {"beatemup", "Beat 'em up"}, {"beatthemup", "Beat 'em up"},
        {"platform", "Platform"}, {"platformer", "Platform"},
        {"puzzle", "Puzzle"}, {"action", "Action"}, {"actionadventure", "Action-adventure"},
        {"adventure", "Adventure"}, {"textadventure", "Text adventure"}, {"interactivefiction", "Text adventure"},
        {"graphicadventure", "Graphic adventure"}, {"arcade", "Arcade"},
        {"racing", "Racing"}, {"driving", "Racing"}, {"sport", "Sports"}, {"sports", "Sports"},
        {"strategy", "Strategy"}, {"simulation", "Simulation"}, {"sim", "Simulation"},
        {"roleplaying", "Role-playing"}, {"rpg", "Role-playing"},
        {"fighting", "Fighting"}, {"maze", "Maze"}, {"pinball", "Pinball"},
        {"board", "Board game"}, {"boardgame", "Board game"}, {"card", "Card game"}, {"cardgame", "Card game"},
        {"educational", "Educational"}, {"education", "Educational"}, {"edutainment", "Educational"},
        {"runandgun", "Run and gun"}, {"quiz", "Quiz"}, {"trivia", "Quiz"}, {"music", "Music"},
        {"casino", "Casino"}, {"gambling", "Casino"}, {"breakout", "Breakout"}
    }

    ' A spelling-proof key: lower case letters and digits only, without a trailing "game"
    Private Shared Function GenreKey(genre As String) As String
        Dim key As String = System.Text.RegularExpressions.Regex.Replace(genre.ToLowerInvariant(), "[^a-z0-9]", "")
        For Each ending In {"videogames", "videogame", "games", "game"}
            If key.Length > ending.Length AndAlso key.EndsWith(ending) Then
                key = key.Substring(0, key.Length - ending.Length)
                Exit For
            End If
        Next
        If key.EndsWith("s") AndAlso _genreNames.ContainsKey(key.Substring(0, key.Length - 1)) Then key = key.Substring(0, key.Length - 1)
        Return key
    End Function

    Private Shared Function CanonicalGenre(genre As String) As String
        Dim trimmed As String = genre.Trim().Replace("’", "'").Replace("_", " ")
        If trimmed = "" Then Return ""
        Dim named As String = Nothing
        If _genreNames.TryGetValue(GenreKey(trimmed), named) Then Return named
        ' Not a known genre: tidy the spelling, capital first letter only
        trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, "\s+", " ")
        Return Char.ToUpperInvariant(trimmed(0)) & trimmed.Substring(1).ToLowerInvariant()
    End Function

    ' Every genre in use, worked out once when the games' details are loaded (they are
    ' read again whenever a genre changes) rather than on every right-click
    Private _knownGenres As List(Of String) = Nothing

    Private Function KnownGenres() As List(Of String)
        If _knownGenres Is Nothing Then
            _knownGenres = _gameDetails.Values.SelectMany(Function(d) GenresOf(d)).
                Concat(_genreNames.Values).
                Distinct(StringComparer.OrdinalIgnoreCase).
                OrderBy(Function(g) g, StringComparer.OrdinalIgnoreCase).ToList()
        End If
        Return _knownGenres
    End Function

    ' Edit Mode: sets lines such as "Year: 1986" in a game's info file, making the
    ' file if there is none. A value of Nothing leaves its line alone; an empty value
    ' empties the line (and takes away a Memory line, so the scan's value shows again).
    Private Sub WriteInfoFields(plainName As String, fields As Dictionary(Of String, String))
        Dim infoPath As String = InfoSlot(_currentSystem).PathFor(plainName, ".txt")
        Directory.CreateDirectory(Path.GetDirectoryName(infoPath))
        Dim lines As New List(Of String)
        If File.Exists(infoPath) Then
            lines.AddRange(File.ReadAllLines(infoPath))
        Else
            lines.AddRange({$"Title: {CleanTitle(plainName)}", $"System: {_systemNames(_currentSystem)}", "Publisher: ", "Developer: ",
                            "Year: ", "Genre: ", "Players: ", "Controls: ", "Loading: ", "", "About:", ""})
        End If

        For Each pair In fields
            If pair.Value Is Nothing Then Continue For
            ' The detail lines are all those above "About:", the same lines the list reads
            Dim aboutLine As Integer = lines.FindIndex(Function(l) l.StartsWith("About:", StringComparison.OrdinalIgnoreCase))
            If aboutLine < 0 Then aboutLine = lines.Count
            Dim prefix As String = pair.Key & ":"
            Dim field As String = pair.Key
            Dim matches As List(Of Integer) = Enumerable.Range(0, aboutLine).
                Where(Function(i) IsInfoLine(lines(i), field)).ToList()

            ' A repeated line (an earlier version could add one) is taken out, keeping the first
            For position As Integer = matches.Count - 1 To 1 Step -1
                lines.RemoveAt(matches(position))
            Next

            If matches.Count > 0 Then
                If pair.Value = "" AndAlso pair.Key = "Memory" Then
                    lines.RemoveAt(matches(0))
                Else
                    lines(matches(0)) = (prefix & " " & pair.Value).TrimEnd()
                End If
            ElseIf pair.Value <> "" Then
                ' After the last "Name: value" line above About:, else at the end of that part
                Dim lastDetail As Integer = -1
                For index As Integer = 0 To aboutLine - 1
                    If System.Text.RegularExpressions.Regex.IsMatch(lines(index), "^\s*[A-Za-z][A-Za-z ]{0,24}:") Then lastDetail = index
                Next
                Dim insertAt As Integer = If(lastDetail >= 0, lastDetail + 1, aboutLine)
                lines.Insert(insertAt, prefix & " " & pair.Value)
            End If
        Next
        File.WriteAllLines(infoPath, lines)
    End Sub

    ' True for a line such as "Genre: Platform" or "genre : Platform" for the field Genre,
    ' matched the same way the list reads them
    Private Shared Function IsInfoLine(line As String, field As String) As Boolean
        Dim colon As Integer = line.IndexOf(":"c)
        Return colon > 0 AndAlso String.Equals(line.Substring(0, colon).Trim(), field, StringComparison.OrdinalIgnoreCase)
    End Function

    ' One line's value from a game's info file, or "" if there is none
    Private Function InfoValue(plainName As String, field As String) As String
        Try
            Dim infoPath As String = InfoSlot(_currentSystem).PathFor(plainName, ".txt")
            If Not File.Exists(infoPath) Then Return ""
            For Each line In File.ReadLines(infoPath)
                If line.StartsWith("About:", StringComparison.OrdinalIgnoreCase) Then Exit For
                If IsInfoLine(line, field) Then Return line.Substring(line.IndexOf(":"c) + 1).Trim()
            Next
        Catch ex As IOException
            Debug.WriteLine($"Could not read the info for {plainName}: {ex.Message}")
        End Try
        Return ""
    End Function

    ' Editing the info and manual text in place (Edit Mode)
    '
    ' The box shows the info file, then the manual below a divider line. Changes are
    ' saved when the box loses focus or another game is picked: text above the divider
    ' goes to the info file and text below it to the manual.

    Private Const MANUAL_DIVIDER As String = "---------- Manual ----------"
    Private Const NO_TEXT_YET As String = "No information or manual available."
    Private _shownTextGame As String = ""
    Private _shownHadInfo As Boolean = False
    Private _shownHadManual As Boolean = False

    ' ---- How an info file is shown: the details that have something in them, one per
    ' line; then the description in paragraphs; then where it came from. The file itself
    ' keeps its "Name: value" lines, then "About:" and the description.

    Private Shared ReadOnly _detailLine As New System.Text.RegularExpressions.Regex("^\s*[A-Za-z][A-Za-z ]{0,24}:")

    ' marks: the game's side, tape, part and cheat marks, added to the Title line if
    ' it lacks them (files written before they were kept there)
    Private Shared Function FormatInfoForDisplay(fileText As String, Optional marks As List(Of String) = Nothing) As String
        Dim lines = fileText.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(vbLf(0))
        Dim details As New List(Of String)
        Dim body As New List(Of String)
        Dim sources As New List(Of String)
        Dim inAbout As Boolean = False
        For Each rawLine In lines
            Dim line As String = rawLine.TrimEnd()
            If Not inAbout Then
                If line.StartsWith("About:", StringComparison.OrdinalIgnoreCase) Then
                    inAbout = True
                    Dim rest As String = line.Substring(6).Trim()
                    If rest <> "" Then body.Add(rest)
                ElseIf _detailLine.IsMatch(line) Then
                    ' Only details that have something in them
                    If line.Substring(line.IndexOf(":"c) + 1).Trim() <> "" Then
                        If marks IsNot Nothing AndAlso line.TrimStart().StartsWith("Title:", StringComparison.OrdinalIgnoreCase) Then
                            For Each mark In marks
                                If line.IndexOf(mark, StringComparison.OrdinalIgnoreCase) < 0 Then line = line.TrimEnd() & $" ({mark})"
                            Next
                        End If
                        details.Add(line.Trim())
                    End If
                ElseIf line.Trim() <> "" Then
                    body.Add(line)     ' free text above About: is kept as description
                End If
            ElseIf line.StartsWith("Source:", StringComparison.OrdinalIgnoreCase) Then
                sources.Add(line.Trim())
            Else
                body.Add(line)
            End If
        Next
        Dim parts As New List(Of String)
        If details.Count > 0 Then parts.Add(String.Join(Environment.NewLine, details))
        Dim paragraphs = SplitIntoParagraphs(String.Join(vbLf, body))
        If paragraphs.Count > 0 Then parts.Add(String.Join(Environment.NewLine & Environment.NewLine, paragraphs))
        If sources.Count > 0 Then parts.Add(String.Join(Environment.NewLine, sources))
        Return String.Join(Environment.NewLine & Environment.NewLine, parts)
    End Function

    ' Paragraphs as written, and a long one split every few sentences so the
    ' description is not one solid block
    Private Shared Function SplitIntoParagraphs(text As String) As List(Of String)
        Dim result As New List(Of String)
        Dim written = System.Text.RegularExpressions.Regex.Split(text.Trim(), "\n\s*\n")
        For Each paragraph In written
            Dim tidy As String = paragraph.Trim()
            If tidy = "" Then Continue For
            If tidy.Length <= 400 OrElse tidy.Contains(vbLf) Then
                result.Add(tidy.Replace(vbLf, Environment.NewLine))
                Continue For
            End If
            Dim sentences = System.Text.RegularExpressions.Regex.Split(tidy, "(?<=[.!?])\s+(?=[A-Z0-9""'(])")
            Dim current As New System.Text.StringBuilder()
            Dim count As Integer = 0
            For Each sentence In sentences
                If current.Length > 0 Then current.Append(" ")
                current.Append(sentence.Trim())
                count += 1
                If count >= 3 AndAlso current.Length >= 250 Then
                    result.Add(current.ToString())
                    current.Clear()
                    count = 0
                End If
            Next
            If current.Length > 0 Then result.Add(current.ToString())
        Next
        Return result
    End Function

    ' Edit Mode saves what is shown: the "Name: value" lines at the top become the
    ' details again, the rest the description after "About:", with any Source: lines last
    Private Shared Function InfoTextForSaving(shown As String) As String
        Dim lines = shown.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(vbLf(0)).ToList()
        Dim index As Integer = 0
        While index < lines.Count AndAlso lines(index).Trim() = ""
            index += 1
        End While
        Dim details As New List(Of String)
        While index < lines.Count AndAlso lines(index).Trim() <> "" AndAlso _detailLine.IsMatch(lines(index)) AndAlso
              Not lines(index).TrimStart().StartsWith("About:", StringComparison.OrdinalIgnoreCase)
            details.Add(lines(index).Trim())
            index += 1
        End While
        Dim body As New List(Of String)
        Dim sources As New List(Of String)
        For Each line In lines.Skip(index)
            If line.TrimStart().StartsWith("Source:", StringComparison.OrdinalIgnoreCase) Then
                sources.Add(line.Trim())
            ElseIf line.TrimStart().StartsWith("About:", StringComparison.OrdinalIgnoreCase) Then
                Dim rest As String = line.TrimStart().Substring(6).Trim()
                If rest <> "" Then body.Add(rest)
            Else
                body.Add(line.TrimEnd())
            End If
        Next
        Dim result As New List(Of String)(details)
        result.Add("")
        result.Add("About:")
        result.Add(String.Join(vbLf, body).Trim())
        If sources.Count > 0 Then
            result.Add("")
            result.AddRange(sources)
        End If
        Return String.Join(vbLf, result).TrimEnd()
    End Function

    Private Sub SaveShownText()
        If Not _editModeEnabled OrElse Not RichTextBox1.Modified OrElse _shownTextGame = "" Then Return
        RichTextBox1.Modified = False

        Dim label As String = _shownTextGame
        Dim gameFile As String = Nothing
        Dim plainName As String = If(_gameFiles.TryGetValue(label, gameFile), Path.GetFileNameWithoutExtension(gameFile), label)
        Dim text As String = RichTextBox1.Text.TrimStart(ControlChars.Cr, ControlChars.Lf)
        If text.Trim() = NO_TEXT_YET Then Return

        Dim infoText As String = Nothing
        Dim manualText As String = Nothing
        Dim divider As Integer = text.IndexOf(MANUAL_DIVIDER, StringComparison.Ordinal)
        If divider >= 0 Then
            infoText = text.Substring(0, divider).TrimEnd()
            manualText = text.Substring(divider + MANUAL_DIVIDER.Length).Trim(ControlChars.Cr, ControlChars.Lf)
        ElseIf _shownHadManual AndAlso Not _shownHadInfo Then
            manualText = text
        Else
            infoText = text
        End If

        Try
            If infoText IsNot Nothing Then
                Dim infoPath As String = InfoSlot(_currentSystem).PathFor(plainName, ".txt")
                Directory.CreateDirectory(Path.GetDirectoryName(infoPath))
                infoText = InfoTextForSaving(infoText)
                File.WriteAllText(infoPath, infoText.Replace(vbLf, vbCrLf).Replace(vbCr & vbCrLf, vbCrLf) & vbCrLf)
            End If
            If manualText IsNot Nothing Then
                Dim manualPath As String = ManualSlot(_currentSystem).PathFor(plainName, ".txt")
                Directory.CreateDirectory(Path.GetDirectoryName(manualPath))
                File.WriteAllText(manualPath, manualText.Replace(vbLf, vbCrLf).Replace(vbCr & vbCrLf, vbCrLf) & vbCrLf)
            End If
        Catch ex As Exception
            MessageBox.Show($"Could not save the text: {ex.Message}", "Save", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End Try

        ' The year, publisher and genre may have changed
        RefreshDetails()
    End Sub

    ' Re-reads the info details (only changed files are opened) and redraws the list
    Private Sub RefreshDetails()
        LoadGameDetails()
        RefreshViewChoices()
        ListBox1.Invalidate()
    End Sub

    ' Importing a folder of games (Edit menu)
    '
    ' Copies the games in a folder into this system's games folder in the background,
    ' then the fast load check runs, and after it the images and info are downloaded
    ' for the new games only. The window stays usable throughout.

    Private _pendingDownloadFiles As HashSet(Of String) = Nothing

    Private Async Sub ImportGamesFromFolder()
        If _bulkCopying Then
            MessageBox.Show("An import is already running.", "Import Games", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim sourceRoot As String
        Using folderDialog As New FolderBrowserDialog() With {.Description = $"Choose a folder of {_systemNames(_currentSystem)} games to import"}
            If folderDialog.ShowDialog(Me) <> DialogResult.OK Then Return
            sourceRoot = folderDialog.SelectedPath
        End Using
        If SameFolder(sourceRoot, GameFolder) OrElse
           Path.GetFullPath(sourceRoot).StartsWith(Path.GetFullPath(GameFolder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
            MessageBox.Show("That folder is already part of the games folder.", "Import Games", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        Dim pairs As New List(Of KeyValuePair(Of String, String))
        Dim targetRoot As String = GameFolder
        ShowActivity("Looking for games...")
        Await Task.Run(Sub()
                           Try
                               For Each source In Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                                   If IsGameFile(source) Then
                                       pairs.Add(New KeyValuePair(Of String, String)(source, Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source))))
                                   End If
                               Next
                           Catch ex As Exception
                               Debug.WriteLine($"Could not read {sourceRoot}: {ex.Message}")
                           End Try
                       End Sub)
        Await CopyGamesInBackground(pairs)
    End Sub

    ' Copies (source, target) pairs off the window's thread, skipping files already there,
    ' then refreshes the list once and starts the follow-up checks and downloads
    Private Async Function CopyGamesInBackground(pairs As List(Of KeyValuePair(Of String, String))) As Task(Of Integer)
        Dim copiedFiles As New List(Of String)
        Dim skipped As Integer = 0
        Dim failed As Integer = 0
        _bulkCopying = True
        Try
            Await Task.Run(Sub()
                               For index As Integer = 0 To pairs.Count - 1
                                   If _formClosing Then Exit For
                                   Dim pair = pairs(index)
                                   Try
                                       If File.Exists(pair.Value) Then
                                           skipped += 1
                                       Else
                                           Directory.CreateDirectory(Path.GetDirectoryName(pair.Value))
                                           File.Copy(pair.Key, pair.Value)
                                           copiedFiles.Add(pair.Value)
                                       End If
                                   Catch ex As Exception
                                       failed += 1
                                       Debug.WriteLine($"Could not copy {pair.Key}: {ex.Message}")
                                   End Try
                                   If index Mod 20 = 0 Then
                                       Dim shown As Integer = index + 1
                                       Try
                                           Me.BeginInvoke(Sub() ShowActivity($"Copying games: {shown:N0} of {pairs.Count:N0}"))
                                       Catch ex As InvalidOperationException
                                           ' The form is closing
                                       End Try
                                   End If
                               Next
                           End Sub)
        Finally
            _bulkCopying = False
        End Try
        If _formClosing Then Return copiedFiles.Count

        ShowActivity("")
        LoadGames()

        Dim summary As String = $"Copied {copiedFiles.Count:N0} game file(s) into {GameFolder}."
        If skipped > 0 Then summary &= $" {skipped:N0} were already there."
        If failed > 0 Then summary &= $" {failed:N0} could not be copied."
        If copiedFiles.Count > 0 Then
            summary &= Environment.NewLine & Environment.NewLine &
                "They are being checked for fast load now. Their images and info will download after that."
            AfterImport(copiedFiles)
        End If
        MessageBox.Show(Me, summary, "Import Games", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Return copiedFiles.Count
    End Function

    ' Downloads images and info for newly imported games once their fast load check is done
    Private Sub AfterImport(newFiles As IEnumerable(Of String))
        If _pendingDownloadFiles Is Nothing Then _pendingDownloadFiles = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        _pendingDownloadFiles.UnionWith(newFiles)
        If Not _scanRunning Then StartPendingDownload()
    End Sub

    Private Sub StartPendingDownload()
        If _pendingDownloadFiles Is Nothing OrElse _pendingDownloadFiles.Count = 0 Then Return
        If _assetProcess IsNot Nothing AndAlso Not _assetProcess.HasExited Then Return ' tried again when that one ends
        Dim files = _pendingDownloadFiles
        _pendingDownloadFiles = Nothing
        StartAssetDownload("case,title,screen", True, files)
    End Sub

    Private _bulkCopying As Boolean = False

    ' Game file helpers

    Private Function IsGameFile(filePath As String) As Boolean
        Return _allowedGameExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
    End Function

    Private Function FindGameFile(gameName As String) As String
        Dim filePath As String = Nothing
        If _gameFiles.TryGetValue(gameName, filePath) AndAlso File.Exists(filePath) Then
            Return filePath
        End If
        Return Nothing
    End Function

    Private Sub RebuildGameIndex()
        _gameFiles.Clear()

        ' One listing gives each file's size and date as well, so nothing is opened here
        _fileStamps.Clear()
        Dim listed As New List(Of String)
        For Each info In New DirectoryInfo(GameFolder).EnumerateFiles("*", SearchOption.AllDirectories)
            If IsGameFile(info.FullName) AndAlso Not InOtherSystemFolder(info.FullName) Then
                listed.Add(info.FullName)
                _fileStamps(info.FullName) = New KeyValuePair(Of Long, Long)(info.Length, info.LastWriteTimeUtc.Ticks)
            End If
        Next
        Dim groups = listed.GroupBy(Function(f) Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)

        For Each sameName In groups
            Dim members = sameName.OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase).ToList()
            Dim folderCount = members.Select(Function(f) Path.GetFileName(Path.GetDirectoryName(f))).
                Distinct(StringComparer.OrdinalIgnoreCase).Count()

            For Each filePath In members
                Dim label As String = Path.GetFileNameWithoutExtension(filePath)
                If members.Count > 1 Then
                    Dim tag As String = If(folderCount > 1,
                                           Path.GetFileName(Path.GetDirectoryName(filePath)),
                                           Path.GetExtension(filePath).TrimStart("."c).ToUpperInvariant())
                    label &= $" [{tag}]"
                End If

                Dim uniqueLabel As String = label
                Dim suffix As Integer = 2
                While _gameFiles.ContainsKey(uniqueLabel)
                    uniqueLabel = $"{label} ({suffix})"
                    suffix += 1
                End While
                _gameFiles(uniqueLabel) = filePath
            Next
        Next

        CarryOverMovedGames()
    End Sub

    ' Moved or renamed games keep their images, info and settings. Each game is
    ' recognised by its size, date and the start of its contents, so a file moved
    ' to another folder (even another system's folder) is still the same game.
    Private ReadOnly Property _gameLocationsPath As String
        Get
            Return Path.Combine(SystemSettingsFolder(_currentSystem), "game_locations.txt")
        End Get
    End Property
    Private ReadOnly _fingerprints As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Private Function GameFingerprint(gameFile As String) As String
        Dim stamp = FileStamp(gameFile)
        Dim cacheKey As String = $"{gameFile}|{stamp.Key}|{stamp.Value}"
        Dim known As String = Nothing
        If _fingerprints.TryGetValue(cacheKey, known) Then Return known

        Dim start(4095) As Byte
        Dim count As Integer
        Using stream As New FileStream(gameFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            count = stream.Read(start, 0, start.Length)
        End Using
        If count < start.Length Then Array.Resize(start, count)
        Dim digest As Byte() = Security.Cryptography.SHA1.HashData(start)
        Dim fingerprint As String = $"{stamp.Key}-{stamp.Value}-{Convert.ToHexString(digest, 0, 8)}"
        _fingerprints(cacheKey) = fingerprint
        _fingerprintsChanged = True
        Return fingerprint
    End Function

    Private Sub CarryOverMovedGames()
        ' Where every game is now: fingerprint to (file, list label)
        Dim current As New Dictionary(Of String, List(Of KeyValuePair(Of String, String)))
        For Each entry In _gameFiles
            Try
                Dim fingerprint As String = GameFingerprint(entry.Value)
                If Not current.ContainsKey(fingerprint) Then current(fingerprint) = New List(Of KeyValuePair(Of String, String))
                current(fingerprint).Add(New KeyValuePair(Of String, String)(entry.Value, entry.Key))
            Catch ex As IOException
                Debug.WriteLine($"Could not read {entry.Value}: {ex.Message}")
            End Try
        Next

        ' Where they were last time
        Dim previous As New Dictionary(Of String, List(Of String()))
        Dim previousLines As String() = {}
        If File.Exists(_gameLocationsPath) Then
            Try
                previousLines = File.ReadAllLines(_gameLocationsPath)
                For Each line In previousLines
                    Dim parts = line.Split(vbTab(0))
                    If parts.Length <> 4 Then Continue For
                    If Not previous.ContainsKey(parts(0)) Then previous(parts(0)) = New List(Of String())
                    previous(parts(0)).Add(parts)
                Next
            Catch ex As IOException
                Debug.WriteLine($"Could not read game locations: {ex.Message}")
            End Try
        End If

        Dim settingsChanged As Boolean = False
        Dim nowPresent = _gameFiles.Values.Select(Function(f) GameSystem(f) & "|" & Path.GetFileNameWithoutExtension(f)).
            ToHashSet(StringComparer.OrdinalIgnoreCase)

        For Each entry In previous
            Dim found As List(Of KeyValuePair(Of String, String)) = Nothing
            ' Only when exactly one game had and has these contents; copies are left alone
            If entry.Value.Count <> 1 OrElse Not current.TryGetValue(entry.Key, found) OrElse found.Count <> 1 Then Continue For

            Dim oldSystem As String = entry.Value(0)(1)
            Dim oldName As String = entry.Value(0)(2)
            Dim oldLabel As String = entry.Value(0)(3)
            Dim newFile As String = found(0).Key
            Dim newLabel As String = found(0).Value
            Dim newSystem As String = GameSystem(newFile)
            Dim newName As String = Path.GetFileNameWithoutExtension(newFile)

            If (newSystem <> oldSystem OrElse newName <> oldName) AndAlso Not nowPresent.Contains(oldSystem & "|" & oldName) Then
                MoveAssets(oldName, oldSystem, newName, newSystem)
            End If

            Dim savedType As Integer
            If newLabel <> oldLabel AndAlso Not _gameFiles.ContainsKey(oldLabel) AndAlso
               Not _fastLoadTypes.ContainsKey(newLabel) AndAlso _fastLoadTypes.TryGetValue(oldLabel, savedType) Then
                _fastLoadTypes.Remove(oldLabel)
                _fastLoadTypes(newLabel) = savedType
                settingsChanged = True
            End If
            If newLabel <> oldLabel AndAlso Not _gameFiles.ContainsKey(oldLabel) AndAlso _favourites.Remove(oldLabel) Then
                _favourites.Add(newLabel)
                settingsChanged = True
            End If
            If newLabel <> oldLabel AndAlso Not _gameFiles.ContainsKey(oldLabel) AndAlso _stopsOn.Remove(oldLabel) Then
                _stopsOn.Add(newLabel)
                settingsChanged = True
            End If
        Next

        If settingsChanged Then SaveFastLoadSettings()
        SaveFingerprintCache()

        ' Games not present right now are remembered too, in case they are part way through a move
        Try
            Dim lines As New List(Of String)(current.SelectMany(
                Function(group) group.Value.Select(
                    Function(game) $"{group.Key}{vbTab}{GameSystem(game.Key)}{vbTab}{Path.GetFileNameWithoutExtension(game.Key)}{vbTab}{game.Value}")))
            For Each entry In previous
                If Not current.ContainsKey(entry.Key) Then lines.AddRange(entry.Value.Select(Function(parts) String.Join(vbTab, parts)))
            Next
            If Not lines.SequenceEqual(previousLines) Then File.WriteAllLines(_gameLocationsPath, lines)
        Catch ex As IOException
            Debug.WriteLine($"Could not save game locations: {ex.Message}")
        End Try
    End Sub


    Private Function IsTapePlaying() As Boolean
        Return tapePlayProcess IsNot Nothing AndAlso Not tapePlayProcess.HasExited
    End Function

    ' The backend may have the control file open for a moment, so retry briefly.
    Private Sub WriteControl(command As String)
        For attempt As Integer = 1 To 10
            Try
                File.WriteAllText(controlFilePath, command)
                Return
            Catch ex As IOException When attempt < 10
                Threading.Thread.Sleep(20)
            Catch ex As UnauthorizedAccessException When attempt < 10
                Threading.Thread.Sleep(20)
            Catch ex As Exception
                Debug.WriteLine($"Could not send tape command '{command}': {ex.Message}")
            End Try
        Next
    End Sub

    Private Sub ShowBackendMessage(line As String)
        If String.IsNullOrEmpty(line) Then Return
        Debug.WriteLine("Tape backend: " & line)
        If Not (line.StartsWith("Error:") OrElse line.StartsWith("Info:")) Then Return
        If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return

        Try
            Me.BeginInvoke(Sub()
                               If line.StartsWith("Info:") Then
                                   ' "Ultra, 5 blocks, 2 stops, Load Time: 0:18": the load time
                                   ' goes beside the game's name, the rest in the status box
                                   Dim info As String = line.Substring(5).Trim()
                                   Dim at As Integer = info.IndexOf(", Load Time:", StringComparison.Ordinal)
                                   If at >= 0 Then
                                       SetLoadTime(info.Substring(at + ", Load Time:".Length).Trim())
                                       info = info.Substring(0, at)
                                   End If
                                   LblTapeStatus.Text = info
                               Else
                                   LblTapeStatus.Text = line
                               End If
                               If line.StartsWith("Error:") Then
                                   TimerResetStatus.Stop()
                                   TimerResetStatus.Start()
                                   ' The status box is too small for most errors, so show them in full
                                   MessageBox.Show(Me, line.Substring(6).Trim(), "Tape Could Not Play",
                                                   MessageBoxButtons.OK, MessageBoxIcon.Warning)
                               End If
                           End Sub)
        Catch ex As InvalidOperationException
            ' The form is closing
        End Try
    End Sub

    ' Per-game fast load, stored after the existing lines of settings.txt

    Private Sub LoadFastLoadSettings()
        _fastLoadTypes.Clear()
        _favourites.Clear()
        _stopsOn.Clear()
        If Not File.Exists(_settingsFilePath) Then Return
        For Each line In File.ReadAllLines(_settingsFilePath).Skip(5)
            If line.StartsWith(FAVOURITE_PREFIX) Then
                _favourites.Add(line.Substring(FAVOURITE_PREFIX.Length))
            ElseIf line.StartsWith(STOPS_ON_PREFIX) Then
                _stopsOn.Add(line.Substring(STOPS_ON_PREFIX.Length))
            ElseIf line.StartsWith(OLD_FAST_LOAD_OFF_PREFIX) Then
                ' From the earlier tick-box version: unticked meant off
                _fastLoadTypes(line.Substring(OLD_FAST_LOAD_OFF_PREFIX.Length)) = TYPE_OFF
            ElseIf line.StartsWith(FAST_LOAD_PREFIX) Then
                Dim entry As String = line.Substring(FAST_LOAD_PREFIX.Length)
                Dim split As Integer = entry.LastIndexOf("|"c)
                Dim fastType As Integer
                If split > 0 AndAlso Integer.TryParse(entry.Substring(split + 1), fastType) AndAlso
                   fastType >= TYPE_UNAVAILABLE AndAlso fastType <= 5 Then
                    _fastLoadTypes(entry.Substring(0, split)) = fastType
                End If
            End If
        Next

        ' Ultra is now the default, so saved Ultra choices are no longer needed
        Dim ultraGames = _fastLoadTypes.Where(Function(g) g.Value = TYPE_ULTRA).Select(Function(g) g.Key).ToList()
        If ultraGames.Count > 0 Then
            For Each gameName In ultraGames
                _fastLoadTypes.Remove(gameName)
            Next
            SaveFastLoadSettings()
        End If
    End Sub

    Private Sub SaveFastLoadSettings()
        Try
            Dim existing As String() = If(File.Exists(_settingsFilePath), File.ReadAllLines(_settingsFilePath), {"", "", "", "True", ""})
            Dim lines As New List(Of String)(existing.Take(5))
            While lines.Count < 5
                lines.Add("")
            End While

            ' Other settings after the first five lines, such as the info folder, are kept
            lines.AddRange(existing.Skip(5).Where(Function(l) Not l.StartsWith(FAST_LOAD_PREFIX) AndAlso
                                                              Not l.StartsWith(OLD_FAST_LOAD_OFF_PREFIX) AndAlso
                                                              Not l.StartsWith(FAVOURITE_PREFIX) AndAlso
                                                              Not l.StartsWith(STOPS_ON_PREFIX) AndAlso
                                                              Not l.StartsWith(NO_STOPS_PREFIX)))
            lines.AddRange(_favourites.OrderBy(Function(g) g, StringComparer.OrdinalIgnoreCase).Select(Function(g) FAVOURITE_PREFIX & g))
            lines.AddRange(_stopsOn.OrderBy(Function(g) g, StringComparer.OrdinalIgnoreCase).Select(Function(g) STOPS_ON_PREFIX & g))
            lines.AddRange(_fastLoadTypes.OrderBy(Function(g) g.Key, StringComparer.OrdinalIgnoreCase).
                           Select(Function(g) $"{FAST_LOAD_PREFIX}{g.Key}|{g.Value}"))
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath))
            File.WriteAllLines(_settingsFilePath, lines)
        Catch ex As IOException
            Debug.WriteLine($"Could not save fast load settings: {ex.Message}")
        End Try
    End Sub

    Private Function FastCheckKey(gameFile As String) As String
        Dim stamp = FileStamp(gameFile)
        Dim machine As String = MachineFor(gameFile)
        ' v4 for the C16: its TAPs are now read at whatever speed they were recorded.
        ' v5 for the C64: parts recorded at a different speed are read. v6 for the
        ' VIC-20: that, tapes in several parts fast load, and its RAM check is better.
        Return $"{CacheVersion(machine)}|{Path.GetFileName(gameFile)}|{stamp.Key}|{stamp.Value}|{machine}"
    End Function

    Private Shared Function CacheVersion(machine As String) As String
        Select Case machine
            Case "c16" : Return "v4"
            Case "vic20" : Return "v6"
            Case Else : Return "v5"
        End Select
    End Function

    ' Scan results are kept on disk so each game is only ever scanned once
    Private Sub LoadFastScanResults()
        If Not File.Exists(_fastScanFilePath) Then Return
        Try
            ' Results from before load times were recorded are dropped, so those games scan again
            Dim kept As New List(Of String)
            Dim lines As String() = File.ReadAllLines(_fastScanFilePath)
            ' VIC-20 results from before the memory column are dropped too, so those scan again
            Dim needMemory As Boolean = ScanFindsMemory
            For Each savedLine In lines
                Dim line As String = savedLine
                Dim parts = line.Split(vbTab(0))
                ' v2 results are kept only where fast load was already available. The
                ' backend now accepts more tapes, so games it turned down are checked again.
                If parts.Length >= 3 AndAlso parts(0).StartsWith("v2|") AndAlso parts(1) = "available" Then
                    parts(0) = "v3|" & parts(0).Substring(3)
                    line = String.Join(vbTab, parts)
                End If
                ' C16 results move to v4 the same way: kept where fast load was available
                If parts.Length >= 3 AndAlso parts(0).StartsWith("v3|") AndAlso parts(0).EndsWith("|c16") Then
                    If parts(1) <> "available" Then Continue For
                    parts(0) = "v4|" & parts(0).Substring(3)
                    line = String.Join(vbTab, parts)
                End If
                ' C64 results move to v5 the same way: kept where fast load was available, so
                ' the rest are checked again with the better tape reading. VIC-20 results
                ' from before v6 are all checked again, as the RAM check changed too.
                If parts.Length >= 3 AndAlso parts(0).EndsWith("|vic20") AndAlso Not parts(0).StartsWith("v6|") Then Continue For
                If parts.Length >= 3 AndAlso parts(0).StartsWith("v3|") AndAlso Not parts(0).EndsWith("|c16") Then
                    If parts(1) <> "available" Then Continue For
                    parts(0) = "v5|" & parts(0).Substring(3)
                    line = String.Join(vbTab, parts)
                End If
                ' Games turned down only because their machine had no fast load yet are checked again
                Dim noFastLoadYet As Boolean = parts.Length >= 2 AndAlso (parts(1) = "C64 only" OrElse parts(1) = "C64 and VIC-20 only")
                If parts.Length >= 3 AndAlso (parts(0).StartsWith("v4|") OrElse parts(0).StartsWith("v5|") OrElse parts(0).StartsWith("v6|")) AndAlso
                   (Not needMemory OrElse parts.Length >= 4) AndAlso Not noFastLoadYet Then
                    _fastCheckCache(parts(0)) = parts(1)
                    _tapeTimes(parts(0)) = parts(2)
                    If parts.Length >= 4 Then _tapeMemory(parts(0)) = parts(3)
                    kept.Add(line)
                End If
            Next
            If Not kept.SequenceEqual(lines) Then File.WriteAllLines(_fastScanFilePath, kept)
        Catch ex As IOException
            Debug.WriteLine($"Could not read fast load scan results: {ex.Message}")
        End Try
    End Sub

    Private Sub StoreFastScanResult(key As String, result As String, times As String, memory As String)
        _fastCheckCache(key) = result
        _tapeTimes(key) = times
        Dim line As String = key & vbTab & result & vbTab & times
        If ScanFindsMemory Then
            _tapeMemory(key) = memory
            line &= vbTab & memory
        End If
        Try
            File.AppendAllText(_fastScanFilePath, line & Environment.NewLine)
        Catch ex As IOException
            Debug.WriteLine($"Could not save fast load scan result: {ex.Message}")
        End Try
    End Sub

    ' Shows what is known about the selected game; the background scan fills in the rest
    Private Sub QueueFastLoadCheck()
        Dim gameName As String = If(ListBox1.SelectedItem?.ToString(), "")
        Dim gameFile As String = If(gameName = "", Nothing, FindGameFile(gameName))
        If gameFile Is Nothing Then
            ShowFastLoadStatus("")
            Return
        End If

        Dim key As String = FastCheckKey(gameFile)
        Dim known As String = Nothing
        If _fastCheckCache.TryGetValue(key, known) OrElse _fastCheckFailures.TryGetValue(key, known) Then
            ShowFastLoadStatus(known)
        Else
            ShowFastLoadStatus(Nothing)
        End If
    End Sub

    ' Scans every game not yet in the results file, without disturbing tape playback
    Private Async Sub StartFastLoadScan()
        If _scanRunning Then
            _scanAgain = True
            Return
        End If

        Dim pending = _gameFiles.Values.
            Select(Function(f) New KeyValuePair(Of String, String)(f, FastCheckKey(f))).
            Where(Function(item) Not _fastCheckCache.ContainsKey(item.Value) AndAlso Not _fastCheckFailures.ContainsKey(item.Value)).
            ToList()
        If ShowMemoryColumn Then ListBox1.Invalidate()   ' every game's key is known now, so RAM can show
        If pending.Count = 0 Then Return

        _scanRunning = True
        Dim scanSystem As String = _currentSystem
        Dim total As Integer = pending.Count
        Dim finished As Integer = 0

        ' Two checkers at a time, each one Python process kept open for the whole scan
        Dim checkers As New System.Collections.Concurrent.ConcurrentBag(Of CheckServer)()
        Await Task.Run(Sub()
                           Parallel.ForEach(pending, New ParallelOptions() With {.MaxDegreeOfParallelism = 2},
                                            Sub(item)
                                                While Not _formClosing AndAlso IsTapeBusy()
                                                    Threading.Thread.Sleep(500)
                                                End While
                                                If _formClosing OrElse scanSystem <> _currentSystem Then Return

                                                Dim checker As CheckServer = Nothing
                                                If Not checkers.TryTake(checker) Then checker = New CheckServer(AddressOf TrackChildProcess)
                                                Dim result As String = RunFastLoadCheck(item.Key, MachineFor(item.Key), checker)
                                                checkers.Add(checker)
                                                Try
                                                    Me.Invoke(Sub()
                                                                  ' Results for a system no longer on show are dropped
                                                                  If scanSystem <> _currentSystem Then Return
                                                                  finished += 1
                                                                  ApplyScanResult(item.Value, result, finished, total)
                                                              End Sub)
                                                Catch ex As InvalidOperationException
                                                    ' The form is closing
                                                End Try
                                            End Sub)
                       End Sub)
        For Each checker In checkers
            checker.Dispose()
        Next

        _scanRunning = False
        If _formClosing Then Return
        If scanSystem <> _currentSystem Then _scanAgain = True
        ShowActivity("")
        If _scanAgain Then
            _scanAgain = False
            StartFastLoadScan()
        End If
        ' Imported games get their images and info once the check is finished
        If Not _scanRunning Then StartPendingDownload()
    End Sub

    Private Sub ApplyScanResult(key As String, result As String, finished As Integer, total As Integer)
        If result.StartsWith(FAST_CHECK_FAILED) Then
            _fastCheckFailures(key) = result
        Else
            Dim parts = result.Split(vbTab(0))
            StoreFastScanResult(key, parts(0), If(parts.Length > 1, parts(1), ""), If(parts.Length > 2, parts(2), "?"))
            If ScanFindsMemory Then
                ListBox1.Invalidate()
                RefilterForRamSoon()
            End If
        End If
        ShowActivity($"Scanning games: {finished} of {total}")
        QueueFastLoadCheck()
    End Sub

    Private Function IsTapeBusy() As Boolean
        Try
            Return IsTapePlaying()
        Catch ex As Exception
            Return True
        End Try
    End Function

    ' Turns the old Game Saves box into the tape status panel. Buttons are removed by
    ' type so this still builds if they are later deleted in the designer.
    Private Sub BuildStatusPanel()
        For Each oldButton In GroupBox1.Controls.OfType(Of Button)().ToList()
            GroupBox1.Controls.Remove(oldButton)
            oldButton.Dispose()
        Next
        GroupBox1.Text = "Tape Status"

        _fastLoadIndicator = New Label() With {.AutoSize = True, .Location = New Point(6, 17), .Font = BtnEject.Font}
        _tapePercent = New Label() With {.Size = New Size(50, 15), .Location = New Point(GroupBox1.Width - 56, 17),
                                         .TextAlign = ContentAlignment.TopRight, .Text = "0%"}
        _tapeProgress = New ProgressBar() With {.Location = New Point(6, 35), .Size = New Size(GroupBox1.Width - 12, 15),
                                                .Minimum = 0, .Maximum = 100, .Style = ProgressBarStyle.Continuous}
        GroupBox1.Controls.AddRange(New Control() {_fastLoadIndicator, _tapePercent, _tapeProgress})
    End Sub

    Private Sub SetTapeProgress(percent As Integer)
        If _tapeProgress Is Nothing Then Return
        _tapeProgress.Value = Math.Max(0, Math.Min(100, percent))
        _tapePercent.Text = $"{_tapeProgress.Value}%"
    End Sub

    ' Sits on the form in the space between the Tape Control and Tape Counter group boxes,
    ' level with the Eject button. Shown in Edit Mode only.
    Private Sub AddFastLoadControls()
        _fastLoadCaption = New Label() With {.Text = "Fast Load", .AutoSize = True, .Visible = _editModeEnabled}
        _fastLoadCombo = New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 96, .Visible = _editModeEnabled}
        For Each fastType In _fastLoadComboOrder
            _fastLoadCombo.Items.Add("Load: " & _fastLoadTypeNames(fastType))
        Next
        Dim fastTips As New ToolTip()
        fastTips.SetToolTip(_fastLoadCombo, "Fast load speed for this game")
        _fastLoadCaption.Visible = False ' the box says what it is
        AddHandler _fastLoadCombo.SelectedIndexChanged, AddressOf FastLoadCombo_SelectedIndexChanged

        Me.Controls.Add(_fastLoadCaption)
        Me.Controls.Add(_fastLoadCombo)

        ' The info and manual text can be edited in Edit Mode, and saves when left
        RichTextBox1.ReadOnly = Not _editModeEnabled
        AddHandler RichTextBox1.Leave, Sub(sender, e) SaveShownText()
        PlaceFastLoadControls()
        _fastLoadCaption.BringToFront()
        _fastLoadCombo.BringToFront()
    End Sub

    ' In the gap between the Tape Control and Tape Counter groups, level with Play
    Private Sub PlaceFastLoadControls()
        If _fastLoadCombo Is Nothing Then Return
        Dim gapLeft As Integer = GroupBox3.Right
        Dim gapWidth As Integer = GroupBox2.Left - GroupBox3.Right
        _fastLoadCombo.Width = Math.Min(100, gapWidth - 6)
        _fastLoadCombo.Left = gapLeft + (gapWidth - _fastLoadCombo.Width) \ 2
        _fastLoadCombo.Top = GroupBox3.Top + BtnEject.Top + (BtnEject.Height - _fastLoadCombo.Height) \ 2
    End Sub

    ' Resizing: the window can be dragged larger or maximised, but not made smaller
    ' than its designed size. The image grows first, keeping its shape, as far as the
    ' height allows with the text box below it kept at its original height; the list
    ' takes the rest of the width, and the tape controls stay along the bottom.

    Private _baseClient As Size
    Private ReadOnly _baseBounds As New Dictionary(Of Control, Rectangle)
    Private _layoutReady As Boolean = False

    Private Sub InitializeResizing()
        For Each item As Control In New Control() {ListBox1, SearchBox, PictureBox1, RichTextBox1,
                                                    GroupBox1, GroupBox2, GroupBox3, Label1, LblCurrentGame}
            _baseBounds(item) = item.Bounds
        Next
        _baseClient = Me.ClientSize
        Me.FormBorderStyle = FormBorderStyle.Sizable
        Me.MaximizeBox = True
        Me.MinimizeBox = True
        Me.MinimumSize = Me.Size
        _layoutReady = True
        AddHandler Me.Resize, Sub(sender, e) LayoutForSize()
    End Sub

    Private Sub LayoutForSize()
        If Not _layoutReady OrElse Me.WindowState = FormWindowState.Minimized Then Return

        Dim extraWidth As Integer = Math.Max(0, Me.ClientSize.Width - _baseClient.Width)
        Dim extraHeight As Integer = Math.Max(0, Me.ClientSize.Height - _baseClient.Height)
        Dim picture As Rectangle = _baseBounds(PictureBox1)
        Dim textArea As Rectangle = _baseBounds(RichTextBox1)
        Dim list As Rectangle = _baseBounds(ListBox1)
        Dim search As Rectangle = _baseBounds(SearchBox)

        Dim columnGap As Integer = picture.Left - list.Right
        Dim rightMargin As Integer = _baseClient.Width - picture.Right
        Dim rowGap As Integer = textArea.Top - picture.Bottom
        Dim areaBottom As Integer = textArea.Bottom + extraHeight

        ' Widest image that keeps its shape and leaves the textArea box its original height
        Dim widestForHeight As Integer = CInt(Math.Floor((areaBottom - picture.Top - rowGap - textArea.Height) * picture.Width / picture.Height))
        Dim imageWidth As Integer = Math.Max(picture.Width, Math.Min(picture.Width + extraWidth, widestForHeight))
        Dim imageHeight As Integer = CInt(Math.Round(imageWidth * picture.Height / picture.Width))
        Dim columnLeft As Integer = Me.ClientSize.Width - rightMargin - imageWidth
        Dim textTop As Integer = picture.Top + imageHeight + rowGap

        PictureBox1.Bounds = New Rectangle(columnLeft, picture.Top, imageWidth, imageHeight)
        RichTextBox1.Bounds = New Rectangle(columnLeft, textTop, imageWidth, areaBottom - textTop)
        PlaceManualButton()

        Dim listWidth As Integer = Math.Max(list.Width, columnLeft - columnGap - list.Left)
        ListBox1.Bounds = New Rectangle(list.Left, list.Top, listWidth, list.Height + extraHeight)
        SearchBox.Bounds = New Rectangle(search.Left, search.Top, search.Width + (listWidth - list.Width), search.Height)
        If _listHeader IsNot Nothing Then _listHeader.Bounds = New Rectangle(ListBox1.Left, ListBox1.Top - HEADER_HEIGHT, ListBox1.Width, HEADER_HEIGHT)
        PlaceFilterControls()

        ' Tape controls: Tape Control stays on the left, Tape Counter and Tape Status on the right
        MoveFromBase(GroupBox3, 0, extraHeight)
        MoveFromBase(GroupBox2, extraWidth, extraHeight)
        MoveFromBase(GroupBox1, extraWidth, extraHeight)
        MoveFromBase(Label1, 0, extraHeight)
        MoveFromBase(LblCurrentGame, 0, extraHeight)
        PlaceFastLoadControls()
        LayoutCurrentGameLine()      ' more room for the name when wider
    End Sub

    Private Sub MoveFromBase(item As Control, across As Integer, down As Integer)
        Dim original As Rectangle = _baseBounds(item)
        item.Location = New Point(original.Left + across, original.Top + down)
    End Sub


    ' The fast load type a game will play with, given what the scan found
    Private Function EffectiveFastLoadType(gameName As String, status As String) As Integer
        Dim stored As Integer
        Dim hasStored As Boolean = _fastLoadTypes.TryGetValue(gameName, stored)
        If hasStored AndAlso stored = TYPE_UNAVAILABLE Then Return TYPE_UNAVAILABLE

        Dim scanned As Boolean = status IsNot Nothing AndAlso status <> ""
        Dim failed As Boolean = scanned AndAlso status.StartsWith(FAST_CHECK_FAILED)
        If scanned AndAlso Not failed AndAlso status <> "available" Then Return TYPE_UNAVAILABLE

        If hasStored Then Return stored
        ' No choice saved for this game: compatible games use Ultra
        Return TYPE_ULTRA
    End Function

    Private Function KnownFastStatus(gameName As String) As String
        Dim gameFile As String = FindGameFile(gameName)
        If gameFile Is Nothing Then Return Nothing
        Dim key As String = FastCheckKey(gameFile)
        Dim known As String = Nothing
        If _fastCheckCache.TryGetValue(key, known) OrElse _fastCheckFailures.TryGetValue(key, known) Then Return known
        Return Nothing
    End Function

    ' The check's answer as "status<TAB>times<TAB>memory", from its output lines
    Private Shared Function ParseCheckOutput(output As String) As String
        Dim outputLines = output.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
        Dim line = outputLines.FirstOrDefault(Function(l) l.StartsWith("Check:"))
        If line Is Nothing Then Return Nothing
        Dim times = outputLines.FirstOrDefault(Function(l) l.StartsWith("Times:"))
        Dim memory = outputLines.FirstOrDefault(Function(l) l.StartsWith("Memory:"))
        Return line.Substring(6).Trim() & vbTab & If(times Is Nothing, "", times.Substring(6).Trim()) &
            vbTab & If(memory Is Nothing, "?", memory.Substring(7).Trim())
    End Function

    ' Through an open checker when there is one; on its own process if that fails
    Private Function RunFastLoadCheck(gameFile As String, machine As String, checker As CheckServer) As String
        If checker IsNot Nothing Then
            Dim output As String = checker.Check(gameFile, machine)
            If output IsNot Nothing Then
                Dim parsed As String = ParseCheckOutput(output)
                If parsed IsNot Nothing Then Return parsed
                Return $"{FAST_CHECK_FAILED}{output}".Trim()
            End If
        End If
        Return RunFastLoadCheck(gameFile, machine)
    End Function

    ' Called from the scan's threads: the list is only changed on the form's own thread
    Private Sub TrackChildProcess(child As Process)
        Try
            If IsHandleCreated AndAlso Not _formClosing Then BeginInvoke(Sub() _childProcesses.Add(child))
        Catch ex As InvalidOperationException
            ' The form is closing
        End Try
    End Sub

    ' One Python process that checks game after game (cbmplay.py --serve). Starting
    ' Python and numpy for every game took longer than most checks.
    Private NotInheritable Class CheckServer
        Implements IDisposable

        Private ReadOnly _track As Action(Of Process)
        Private _process As Process
        Private _broken As Boolean

        Public Sub New(track As Action(Of Process))
            _track = track
        End Sub

        ' The check's output for one game, or Nothing if the checker could not answer
        Public Function Check(gameFile As String, machine As String) As String
            If _broken Then Return Nothing
            Try
                If _process Is Nothing OrElse _process.HasExited Then Start()
                _process.StandardInput.WriteLine(gameFile & vbTab & machine)
                _process.StandardInput.Flush()
                Dim output As New System.Text.StringBuilder()
                Do
                    Dim line As String = _process.StandardOutput.ReadLine()
                    If line Is Nothing Then
                        ' The checker stopped: start a new one for the next game
                        StopProcess()
                        Return Nothing
                    End If
                    If line = "Done:" Then Return output.ToString()
                    output.AppendLine(line)
                Loop
            Catch ex As Exception
                StopProcess()
                Return Nothing
            End Try
        End Function

        Private Sub Start()
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                .Arguments = $"""{Path.Combine(Application.StartupPath, "cbmplay.py")}"" --serve",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardInput = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardInputEncoding = New System.Text.UTF8Encoding(False),
                .StandardOutputEncoding = New System.Text.UTF8Encoding(False)
            }
            Try
                _process = Process.Start(startInfo)
            Catch ex As Exception
                _broken = True
                Throw
            End Try
            AddHandler _process.ErrorDataReceived, Sub(sender, e)
                                                   End Sub
            _process.BeginErrorReadLine()
            _track(_process)
        End Sub

        Private Sub StopProcess()
            If _process Is Nothing Then Return
            Try
                If Not _process.HasExited Then _process.Kill()
            Catch ex As Exception
            End Try
            _process = Nothing
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _process Is Nothing Then Return
            Try
                If Not _process.HasExited Then
                    _process.StandardInput.WriteLine("quit")
                    _process.StandardInput.Flush()
                    If Not _process.WaitForExit(2000) Then _process.Kill()
                End If
            Catch ex As Exception
            End Try
            _process = Nothing
        End Sub
    End Class

    Private Function RunFastLoadCheck(gameFile As String, machine As String) As String
        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                .Arguments = $"""{Path.Combine(Application.StartupPath, "cbmplay.py")}"" ""{gameFile}"" --check --machine {machine}",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }
            Using checkProcess As Process = Process.Start(startInfo)
                Dim errorText = checkProcess.StandardError.ReadToEndAsync()
                Dim output As String = checkProcess.StandardOutput.ReadToEnd()
                checkProcess.WaitForExit()
                Dim parsed As String = ParseCheckOutput(output)
                If parsed IsNot Nothing Then Return parsed
                Return $"{FAST_CHECK_FAILED}Exit code {checkProcess.ExitCode}. {output} {errorText.Result}".Trim()
            End Using
        Catch ex As Exception
            Return FAST_CHECK_FAILED & ex.Message
        End Try
    End Function

    ' status: Nothing while checking, "" with no game, "available", or the reason it is not
    Private Sub ShowFastLoadStatus(status As String)
        _fastLoadStatus = status
        If _fastLoadIndicator Is Nothing Then Return

        Dim gameName As String = If(ListBox1.SelectedItem?.ToString(), "")
        Dim failed As Boolean = status IsNot Nothing AndAlso status.StartsWith(FAST_CHECK_FAILED)
        Dim compatible As Boolean = status = "available" OrElse failed
        Dim fastType As Integer = EffectiveFastLoadType(gameName, status)
        Dim textColor As Color = SystemColors.ControlText
        Dim tip As String = ""

        If status Is Nothing Then
            _fastLoadIndicator.Text = "Fast Load: Checking..."
            textColor = Color.Gray
        ElseIf status = "" Then
            _fastLoadIndicator.Text = ""
        ElseIf fastType = TYPE_UNAVAILABLE Then
            _fastLoadIndicator.Text = "Fast Load: Unavailable"
            If failed Then
                tip = "Check failed: " & status.Substring(FAST_CHECK_FAILED.Length)
            ElseIf compatible Then
                tip = "Marked unavailable in Edit Mode"
            Else
                tip = "Normal speed: " & status
            End If
        ElseIf fastType = TYPE_OFF Then
            _fastLoadIndicator.Text = "Fast Load: Off"
            textColor = Color.DarkOrange
        Else
            _fastLoadIndicator.Text = $"Fast Load: On ({_fastLoadTypeNames(fastType)})"
            textColor = Color.Green
        End If

        _fastLoadIndicator.ForeColor = textColor
        _fastLoadToolTip.SetToolTip(_fastLoadIndicator, tip)

        ' Games the scan rules out can only show Unavailable
        _updatingFastLoadCombo = True
        _fastLoadCombo.Enabled = compatible
        _fastLoadCombo.SelectedIndex = If(status Is Nothing OrElse status = "", -1, Array.IndexOf(_fastLoadComboOrder, fastType))
        _updatingFastLoadCombo = False

        ShowLoadTime(gameName, fastType)
    End Sub

    ' Status line before Play: the speed the game will use, its block count and load time
    Private Sub ShowLoadTime(gameName As String, fastType As Integer)
        If IsTapePlaying() OrElse gameName = "" Then Return
        Dim gameFile As String = FindGameFile(gameName)
        Dim times As String = Nothing
        If gameFile Is Nothing OrElse Not _tapeTimes.TryGetValue(FastCheckKey(gameFile), times) OrElse times = "" Then
            SetLoadTime("")
            LblTapeStatus.Text = "Ready"      ' not checked yet
            Return
        End If

        Dim speedNames As String() = {"Normal", "Fast", "Turbo", "Hyper", "Ultra"}
        Dim wanted As Integer = If(fastType >= 2, fastType - 1, 0)
        For Each entry In times.Split({" "c}, StringSplitOptions.RemoveEmptyEntries).Reverse()
            Dim parts = entry.Split(":"c)
            Dim speed, seconds, blocks As Integer
            If parts.Length = 3 AndAlso Integer.TryParse(parts(0), speed) AndAlso speed <= wanted AndAlso
               Integer.TryParse(parts(1), seconds) AndAlso Integer.TryParse(parts(2), blocks) Then
                LblTapeStatus.Text = $"{speedNames(speed)}, {blocks} blocks"
                SetLoadTime($"{seconds \ 60}:{seconds Mod 60:00}")
                Return
            End If
        Next
    End Sub

    Private Sub FastLoadCombo_SelectedIndexChanged(sender As Object, e As EventArgs)
        If _updatingFastLoadCombo OrElse ListBox1.SelectedItem Is Nothing OrElse _fastLoadCombo.SelectedIndex < 0 Then Return

        ' Ultra is the default, so only other choices are saved
        Dim chosen As Integer = _fastLoadComboOrder(_fastLoadCombo.SelectedIndex)
        If chosen = TYPE_ULTRA Then
            _fastLoadTypes.Remove(ListBox1.SelectedItem.ToString())
        Else
            _fastLoadTypes(ListBox1.SelectedItem.ToString()) = chosen
        End If
        SaveFastLoadSettings()
        ShowFastLoadStatus(_fastLoadStatus)
    End Sub

    ' Tape counter

    ' Returns the counter reading, the reading at the block start, and percent played
    Private Function ReadTapeCounter() As Integer()
        Dim positionFile As String = Path.Combine(Path.GetTempPath(), "cbm_position.txt")
        Try
            If Not File.Exists(positionFile) Then Return Nothing
            Dim parts = File.ReadAllText(positionFile).Split(" "c)
            If parts.Length <> 3 Then Return Nothing
            Return parts.Select(Function(p) Integer.Parse(p)).ToArray()
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ' Tape options

    ' tape_options.txt holds "invert.C16=True", "sine.C64=False" and so on for each
    ' system. Earlier versions had one "invert=" and one "sine=" for every system:
    ' that invert setting now goes to the C16 only (the other machines did not need
    ' it), and that sine setting to every system.
    Private Sub LoadTapeOptions()
        If Not File.Exists(_optionsFilePath) Then Return
        Dim oldInvert As Boolean? = Nothing
        Dim oldSine As Boolean? = Nothing
        Try
            For Each line In File.ReadAllLines(_optionsFilePath)
                Dim parts = line.Split("="c)
                If parts.Length <> 2 Then Continue For
                Dim key As String = parts(0).Trim().ToLowerInvariant()
                Dim isOn As Boolean = parts(1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                If key.StartsWith("invert.") Then
                    _invertBySystem(key.Substring(7).ToUpperInvariant()) = isOn
                    Continue For
                ElseIf key.StartsWith("sine.") Then
                    _sineBySystem(key.Substring(5).ToUpperInvariant()) = isOn
                    Continue For
                ElseIf key.StartsWith("leader.") Then
                    Dim percent As Integer
                    If Integer.TryParse(parts(1).Trim(), percent) AndAlso LEADER_CHOICES.Contains(percent) Then
                        _leaderBySystem(key.Substring(7).ToUpperInvariant()) = percent
                    End If
                    Continue For
                End If
                Select Case key
                    Case "invert" : oldInvert = isOn
                    Case "sine" : oldSine = isOn
                    Case "system"
                        Dim chosenSystem As String = parts(1).Trim().ToUpperInvariant()
                        If _systemFolders.Contains(chosenSystem) Then _currentSystem = chosenSystem
                    Case "image"
                        Dim chosen As String = parts(1).Trim()
                        If _imageTypeFolders.Contains(chosen) Then _imageTypeOption = chosen
                End Select
            Next
        Catch ex As IOException
            Debug.WriteLine($"Could not read tape options: {ex.Message}")
        End Try
        If oldInvert.HasValue AndAlso _invertBySystem.Count = 0 Then _invertBySystem("C16") = oldInvert.Value
        If oldSine.HasValue AndAlso _sineBySystem.Count = 0 Then
            For Each systemKey In _systemFolders
                _sineBySystem(systemKey) = oldSine.Value
            Next
        End If
        UseTapeOptionsOf(_currentSystem)
    End Sub

    ' Makes a system's own invert and sine settings the ones used for playing
    Private Sub UseTapeOptionsOf(gameSystemName As String)
        Dim value As Boolean
        _invertOption = _invertBySystem.TryGetValue(gameSystemName, value) AndAlso value
        _sineOption = _sineBySystem.TryGetValue(gameSystemName, value) AndAlso value
        Dim percent As Integer
        _leaderOption = If(_leaderBySystem.TryGetValue(gameSystemName, percent), percent, 100)
    End Sub

    Private Sub SaveTapeOptions()
        _invertBySystem(_currentSystem) = _invertOption
        _sineBySystem(_currentSystem) = _sineOption
        _leaderBySystem(_currentSystem) = _leaderOption
        Dim lines As New List(Of String)
        For Each systemKey In _systemFolders
            Dim value As Boolean
            lines.Add($"invert.{systemKey}={_invertBySystem.TryGetValue(systemKey, value) AndAlso value}")
            lines.Add($"sine.{systemKey}={_sineBySystem.TryGetValue(systemKey, value) AndAlso value}")
            Dim percent As Integer
            lines.Add($"leader.{systemKey}={If(_leaderBySystem.TryGetValue(systemKey, percent), percent, 100)}")
        Next
        lines.Add($"image={_imageTypeOption}")
        lines.Add($"system={_currentSystem}")
        Try
            File.WriteAllLines(_optionsFilePath, lines)
        Catch ex As IOException
            Debug.WriteLine($"Could not save tape options: {ex.Message}")
        End Try
    End Sub

    Private Function BuildTapeOptionsMenu() As ToolStripMenuItem
        Dim optionsMenu As New ToolStripMenuItem("Tape Options")

        Dim invertItem As New ToolStripMenuItem("Invert Signal") With {.Checked = _invertOption, .CheckOnClick = True}
        AddHandler invertItem.Click, Sub(sender, e)
                                         _invertOption = invertItem.Checked
                                         SaveTapeOptions()
                                     End Sub
        optionsMenu.DropDownItems.Add(invertItem)

        Dim sineItem As New ToolStripMenuItem("Sine Wave Output") With {.Checked = _sineOption, .CheckOnClick = True}
        AddHandler sineItem.Click, Sub(sender, e)
                                       _sineOption = sineItem.Checked
                                       SaveTapeOptions()
                                   End Sub
        optionsMenu.DropDownItems.Add(sineItem)

        ' Short Leader: being tested on real machines, so Normal unless chosen here
        Dim leaderMenu As New ToolStripMenuItem("Short Leader (Test)")
        For Each choice In {New KeyValuePair(Of String, Integer)("Normal", 100),
                            New KeyValuePair(Of String, Integer)("Half", 50),
                            New KeyValuePair(Of String, Integer)("Quarter", 25)}
            Dim percent As Integer = choice.Value
            Dim item As New ToolStripMenuItem(choice.Key)
            AddHandler item.Click, Sub(sender, e)
                                       _leaderOption = percent
                                       SaveTapeOptions()
                                   End Sub
            leaderMenu.DropDownItems.Add(item)
        Next
        AddHandler leaderMenu.DropDownOpening, Sub(sender, e)
                                                   For Each item As ToolStripMenuItem In leaderMenu.DropDownItems
                                                       item.Checked = LEADER_CHOICES(leaderMenu.DropDownItems.IndexOf(item)) = _leaderOption
                                                   Next
                                               End Sub
        optionsMenu.DropDownItems.Add(leaderMenu)

        Return optionsMenu
    End Function

    ' Edit Mode Management

    Private Sub ToggleEditMode(enable As Boolean)
        ' Anything typed so far is kept before editing switches off
        SaveShownText()
        _editModeEnabled = enable

        ' Several games can be picked at once in Edit Mode (Shift or Ctrl click)
        Dim wanted As SelectionMode = If(enable, SelectionMode.MultiExtended, SelectionMode.One)
        If ListBox1.SelectionMode <> wanted Then
            Dim keep As Object = ListBox1.SelectedItem
            ListBox1.SelectionMode = wanted
            If keep IsNot Nothing AndAlso ListBox1.Items.Contains(keep) Then ListBox1.SelectedItem = keep
        End If
        ListBox1.AllowDrop = enable
        PictureBox1.AllowDrop = enable
        RichTextBox1.AllowDrop = enable

        If ListBox1.ContextMenuStrip IsNot Nothing Then
            ListBox1.ContextMenuStrip.Enabled = True ' favourites work outside Edit Mode
        End If

        For Each item In _downloadMenuItems
            item.Enabled = enable
        Next

        If _fastLoadCombo IsNot Nothing Then _fastLoadCombo.Visible = enable
        RichTextBox1.ReadOnly = Not enable

        ' Update menu item text
        UpdateEditModeMenuText()
    End Sub

    Private Sub UpdateEditModeMenuText()
        If _editModeMenuItem IsNot Nothing Then
            _editModeMenuItem.Text = If(_editModeEnabled, "Editor On", "Editor Off")
        End If
    End Sub

    Private Sub EditModeToolStripMenuItem_Click(sender As Object, e As EventArgs)
        ToggleEditMode(Not _editModeEnabled)
    End Sub

    ' Resource Management Methods

    Private Sub ReleaseImageResources()
        If PictureBox1.Image IsNot Nothing Then
            Dim oldImage = PictureBox1.Image
            PictureBox1.Image = Nothing
            oldImage.Dispose()
            _currentImagePath = ""
        End If
    End Sub

    Private Sub ReleaseManualResources()
        RichTextBox1.Text = ""
        _currentManualPath = ""
        _currentManuals.Clear()
        If _manualButton IsNot Nothing Then _manualButton.Enabled = False
    End Sub

    Public Sub PrepareForFileOperation()
        ReleaseImageResources()
        ReleaseManualResources()
        Application.DoEvents()
    End Sub

    ' Safe File Operations

    Public Function SafeDeleteFile(filePath As String, Optional maxRetries As Integer = 2) As Boolean
        For retry = 1 To maxRetries
            Try
                PrepareForFileOperation()
                If File.Exists(filePath) Then
                    File.Delete(filePath)
                    Return True
                End If
                Return False
            Catch ex As Exception When retry < maxRetries
                Threading.Thread.Sleep(100)
            Catch ex As Exception
                Debug.WriteLine($"Failed to delete {filePath}: {ex.Message}")
                Return False
            End Try
        Next
        Return False
    End Function

    Public Function SafeRenameFile(oldPath As String, newPath As String) As Boolean
        Try
            PrepareForFileOperation()
            If File.Exists(oldPath) Then
                Directory.CreateDirectory(Path.GetDirectoryName(newPath))
                File.Move(oldPath, newPath)
                Return True
            End If
            Return False
        Catch ex As Exception
            Debug.WriteLine($"Failed to rename {oldPath} to {newPath}: {ex.Message}")
            Return False
        End Try
    End Function

    ' Drag-and-Drop Handlers (with Edit Mode check)

    Private Sub ListBox1_DragEnter(sender As Object, e As DragEventArgs) Handles ListBox1.DragEnter
        If Not _editModeEnabled Then
            e.Effect = DragDropEffects.None
            Return
        End If

        If e.Data.GetDataPresent(DataFormats.FileDrop) Then
            Dim files() As String = CType(e.Data.GetData(DataFormats.FileDrop), String())
            If files.Any(Function(f) IsGameFile(f)) Then
                e.Effect = DragDropEffects.Copy
            Else
                e.Effect = DragDropEffects.None
            End If
        Else
            e.Effect = DragDropEffects.None
        End If
    End Sub

    Private Sub ListBox1_DragDrop(sender As Object, e As DragEventArgs) Handles ListBox1.DragDrop
        If Not _editModeEnabled Then Return

        Dim files() As String = CType(e.Data.GetData(DataFormats.FileDrop), String())
        If files.Length = 0 Then Return

        Dim gameFiles = files.Where(Function(f) IsGameFile(f)).ToArray()
        If gameFiles.Length = 0 Then Return

        Try
            Dim successCount As Integer = 0
            Dim errorMessages As New List(Of String)

            For Each sourceFile In gameFiles
                Try
                    Dim fileName = Path.GetFileName(sourceFile)
                    Dim destPath = Path.Combine(GameFolder, fileName)

                    If File.Exists(destPath) Then
                        Dim result = MessageBox.Show($"The file {fileName} already exists. Overwrite?",
                                                  "File Exists",
                                                  MessageBoxButtons.YesNo,
                                                  MessageBoxIcon.Question)
                        If result <> DialogResult.Yes Then
                            Continue For
                        End If
                    End If

                    File.Copy(sourceFile, destPath, True)
                    successCount += 1
                Catch ex As Exception
                    errorMessages.Add($"Error copying {Path.GetFileName(sourceFile)}: {ex.Message}")
                End Try
            Next

            LoadGames()

            Dim message = $"{successCount} game(s) added successfully."
            If errorMessages.Count > 0 Then
                message += Environment.NewLine & Environment.NewLine & "Errors:" & Environment.NewLine & String.Join(Environment.NewLine, errorMessages)
            End If

            MessageBox.Show(message, "Import Complete", MessageBoxButtons.OK,
                          If(errorMessages.Count > 0, MessageBoxIcon.Warning, MessageBoxIcon.Information))
        Catch ex As Exception
            MessageBox.Show($"Error processing files: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub InitializeGameFolderWatcher()
        Directory.CreateDirectory(GameFolder)
        _gameFolderWatcher = New FileSystemWatcher() With {
            .Path = GameFolder,
            .NotifyFilter = NotifyFilters.FileName Or NotifyFilters.LastWrite,
            .Filter = "*.*",
            .IncludeSubdirectories = True,
            .EnableRaisingEvents = True
        }
    End Sub

    Private Sub GameFolderWatcher_Changed(sender As Object, e As FileSystemEventArgs) Handles _gameFolderWatcher.Changed, _gameFolderWatcher.Created, _gameFolderWatcher.Deleted, _gameFolderWatcher.Renamed
        If Not IsGameFile(e.FullPath) OrElse _bulkCopying Then Return

        Me.Invoke(Sub()
                      Dim currentSelection As String = If(ListBox1.SelectedItem?.ToString(), String.Empty)
                      Dim currentIndex As Integer = ListBox1.SelectedIndex

                      LoadGames()

                      If Not String.IsNullOrEmpty(currentSelection) Then
                          Dim newIndex = ListBox1.Items.IndexOf(currentSelection)
                          If newIndex >= 0 Then
                              ListBox1.SelectedIndex = newIndex
                          ElseIf currentIndex >= 0 AndAlso currentIndex < ListBox1.Items.Count Then
                              ListBox1.SelectedIndex = currentIndex
                          End If
                      End If
                  End Sub)
    End Sub

    Private Sub ResetBlockLabels()
        LblTapeCounter.Text = "Tape Counter: 000"
        LblZeroedBlock.Text = "Reset at: ---"
        zeroedBlock = -1
        SetTapeProgress(0)
    End Sub

    ' A VIC-20 tape of several parts stops itself before each later part (the player
    ' writes cbm_waiting.txt) until Play is pressed for it
    Private ReadOnly _waitingFile As String = Path.Combine(Path.GetTempPath(), "cbm_waiting.txt")
    Private _statusBeforeWaiting As String = Nothing
    Private Const WAITING_TEXT As String = "Press Play for the next part"   ' short: the status box is small

    Private Sub ShowWaiting(waiting As Boolean)
        If waiting AndAlso _statusBeforeWaiting Is Nothing Then
            _statusBeforeWaiting = LblTapeStatus.Text
            LblTapeStatus.Text = WAITING_TEXT
        ElseIf Not waiting AndAlso _statusBeforeWaiting IsNot Nothing Then
            LblTapeStatus.Text = _statusBeforeWaiting
            _statusBeforeWaiting = Nothing
        End If
    End Sub

    Private Sub UpdateTapeCounter(sender As Object, e As EventArgs)
        If tapePlayProcess IsNot Nothing AndAlso Not tapePlayProcess.HasExited Then
            Dim counter = ReadTapeCounter()
            If counter IsNot Nothing Then
                LblTapeCounter.Text = $"Tape Counter: {counter(0):D3}"
                SetTapeProgress(counter(2))
            End If
            ShowWaiting(File.Exists(_waitingFile))
        Else
            ShowWaiting(False)
            If _tapeProgress IsNot Nothing AndAlso _tapeProgress.Value > 0 Then SetTapeProgress(100)
            ListBox1.Enabled = True
            RunDeleteTempFiles()
            BtnStop.Text = "Stop"
            tapeCounterTimer.Stop()
        End If
    End Sub

    ' The line along the bottom: Current Game: name, then Load Time: m:ss. A long name
    ' scrolls in the room left before the tape counter, so the load time stays put.
    Private _currentGameText As String = ""
    Private _scrollChars As Integer = DISPLAY_CHARS
    Private _loadTimeLabel As Label = Nothing

    Private Sub StartScrollingText(text As String)
        _currentGameText = text
        LayoutCurrentGameLine()
    End Sub

    Private Sub SetLoadTime(time As String)
        If _loadTimeLabel Is Nothing Then Return
        Dim wanted As String = If(time = "", "", $"Load Time: {time}")
        If _loadTimeLabel.Text = wanted Then Return
        _loadTimeLabel.Text = wanted
        LayoutCurrentGameLine()
    End Sub

    Private Sub LayoutCurrentGameLine()
        Dim loadWidth As Integer = 0
        If _loadTimeLabel IsNot Nothing AndAlso _loadTimeLabel.Text <> "" Then
            loadWidth = TextRenderer.MeasureText(_loadTimeLabel.Text, _loadTimeLabel.Font).Width + 16
        End If
        Dim available As Integer = Math.Max(80, GroupBox2.Left - 12 - LblCurrentGame.Left - loadWidth)
        Dim fullWidth As Integer = TextRenderer.MeasureText(_currentGameText, LblCurrentGame.Font).Width
        scrollIndex = 0
        If _currentGameText = "" OrElse fullWidth <= available Then
            scrollTimer.Stop()
            scrollText = _currentGameText
            LblCurrentGame.AutoSize = True
            LblCurrentGame.Text = _currentGameText
        Else
            scrollText = _currentGameText & "    " ' Add gap for readability
            Dim averageWidth As Double = fullWidth / _currentGameText.Length
            _scrollChars = Math.Max(8, CInt(Math.Floor(available / averageWidth)) - 1)
            LblCurrentGame.AutoSize = False
            LblCurrentGame.Size = New Size(available, LblCurrentGame.PreferredHeight)
            LblCurrentGame.Text = scrollText.Substring(0, Math.Min(_scrollChars, scrollText.Length))
            scrollTimer.Start()
        End If
        If _loadTimeLabel IsNot Nothing Then
            Dim nameWidth As Integer = If(LblCurrentGame.AutoSize, LblCurrentGame.PreferredWidth, LblCurrentGame.Width)
            _loadTimeLabel.Location = New Point(LblCurrentGame.Left + nameWidth + 12, LblCurrentGame.Top)
        End If
    End Sub

    Private Sub ScrollTimer_Tick(sender As Object, e As EventArgs) Handles scrollTimer.Tick
        If scrollText.Length <= _scrollChars Then
            LblCurrentGame.Text = scrollText
            scrollTimer.Stop()
            Return
        End If

        Dim loopedText As String = scrollText & scrollText.Substring(0, _scrollChars)
        If scrollIndex + _scrollChars > loopedText.Length Then scrollIndex = 0
        LblCurrentGame.Text = loopedText.Substring(scrollIndex, _scrollChars)
        scrollIndex += 1
    End Sub

    ' Scanning, downloading and the like, at the right of the menu bar, so picking a
    ' game (which fills the tape status box) does not hide them. Blank when idle.
    Private _activityLabel As ToolStripLabel = Nothing

    Private Sub ShowActivity(text As String)
        If _activityLabel Is Nothing OrElse Me.IsDisposed Then Return
        If Me.InvokeRequired Then
            Try
                Me.BeginInvoke(Sub() ShowActivity(text))
            Catch ex As InvalidOperationException
                ' The form is closing
            End Try
            Return
        End If
        _activityLabel.Text = text
    End Sub

    ' The bottom line's load time, the activity label, and a status box that ends
    ' long text with … (the whole of it shows when the mouse is over it)
    Private ReadOnly _statusTip As New ToolTip()

    Private Sub InitializeStatusLines()
        _loadTimeLabel = New Label() With {
            .AutoSize = True, .Font = LblCurrentGame.Font, .ForeColor = LblCurrentGame.ForeColor,
            .BackColor = LblCurrentGame.BackColor, .Location = LblCurrentGame.Location, .Text = ""}
        Me.Controls.Add(_loadTimeLabel)
        _loadTimeLabel.BringToFront()

        LblTapeStatus.AutoSize = False
        LblTapeStatus.AutoEllipsis = True
        LblTapeStatus.Size = New Size(Math.Max(60, GroupBox1.ClientSize.Width - LblTapeStatus.Left - 4), LblTapeStatus.PreferredHeight)
        AddHandler LblTapeStatus.TextChanged, Sub(sender, e) _statusTip.SetToolTip(LblTapeStatus, LblTapeStatus.Text)
    End Sub

    ' The games picked in the list: one, or several in Edit Mode (Shift or Ctrl click)
    Private Function SelectedGames() As List(Of String)
        Return ListBox1.SelectedItems.Cast(Of Object)().Select(Function(item) item.ToString()).ToList()
    End Function

    ' RAM sizes offered for this system in the right-click menu and Edit Details
    Private Function RamSizesForSystem() As String()
        If IsC16System Then Return {"16K", "32K", "64K"}
        Return {"None", "3K", "8K", "16K", "24K", "32K", "A000"}
    End Function

    Private Sub InitializeContextMenu()
        Dim contextMenu As New ContextMenuStrip()

        Dim addFavourite As New ToolStripMenuItem("Add to Favourites")
        AddHandler addFavourite.Click, Sub(sender, e) SetFavourites(SelectedGames(), True)
        Dim removeFavourite As New ToolStripMenuItem("Remove from Favourites")
        AddHandler removeFavourite.Click, Sub(sender, e) SetFavourites(SelectedGames(), False)
        contextMenu.Items.Add(addFavourite)
        contextMenu.Items.Add(removeFavourite)

        Dim editSeparator As New ToolStripSeparator()
        contextMenu.Items.Add(editSeparator)

        Dim detailsItem As New ToolStripMenuItem("Edit Details…")
        AddHandler detailsItem.Click, Sub(sender, e) EditDetails(SelectedGames())
        contextMenu.Items.Add(detailsItem)

        ' Just the chosen games, and straight away: games not found recently are asked again
        Dim downloadImagesItem As New ToolStripMenuItem("Download Images")
        AddHandler downloadImagesItem.Click, Sub(sender, e) DownloadForGames(SelectedGames(), "case,title,screen", False)
        contextMenu.Items.Add(downloadImagesItem)
        Dim downloadInfoItem As New ToolStripMenuItem("Download Info")
        AddHandler downloadInfoItem.Click, Sub(sender, e) DownloadForGames(SelectedGames(), "", True)
        contextMenu.Items.Add(downloadInfoItem)

        ' Genre and RAM: their lists are made as the menu opens
        Dim genreMenu As New ToolStripMenuItem("Genre")
        genreMenu.DropDownItems.Add(New ToolStripMenuItem("(loading)"))    ' so it shows its arrow
        ' Filled only when pointed at, for the games right-clicked (kept in its Tag)
        AddHandler genreMenu.DropDownOpening, Sub(sender, e)
                                                  Dim forGames = TryCast(genreMenu.Tag, List(Of String))
                                                  If forGames IsNot Nothing Then FillGenreMenu(genreMenu, forGames)
                                              End Sub
        contextMenu.Items.Add(genreMenu)
        Dim ramMenu As New ToolStripMenuItem("RAM")
        contextMenu.Items.Add(ramMenu)

        ' Off unless turned on here for a game: then the tape stops where it was stopped
        ' originally, until Play is pressed
        Dim stopsItem As New ToolStripMenuItem("Stop Tape Between Parts")
        AddHandler stopsItem.Click, Sub(sender, e) SetTapeStops(SelectedGames(), Not stopsItem.Checked)
        contextMenu.Items.Add(stopsItem)

        Dim fileSeparator As New ToolStripSeparator()
        contextMenu.Items.Add(fileSeparator)

        Dim renameItem As New ToolStripMenuItem("Rename")
        AddHandler renameItem.Click, AddressOf RenameGame
        contextMenu.Items.Add(renameItem)

        Dim addManualItem As New ToolStripMenuItem("Add Manual…")
        AddHandler addManualItem.Click, Sub(sender, e) AddManual(SelectedGames())
        contextMenu.Items.Add(addManualItem)

        Dim moveItem As New ToolStripMenuItem("Move")
        AddHandler moveItem.Click, AddressOf MoveGame
        contextMenu.Items.Add(moveItem)

        Dim deleteItem As New ToolStripMenuItem("Delete")
        AddHandler deleteItem.Click, AddressOf DeleteGame
        contextMenu.Items.Add(deleteItem)

        Dim convertItem As New ToolStripMenuItem("Convert to PRG")
        AddHandler convertItem.Click, Sub(sender, e) ConvertToPrg(SelectedGames())
        contextMenu.Items.Add(convertItem)

        ' Shows the right favourites items for the games, and the editing items in Edit Mode only
        AddHandler contextMenu.Opening, Sub(sender, e)
                                            Dim games = SelectedGames()
                                            If games.Count = 0 Then
                                                e.Cancel = True
                                                Return
                                            End If
                                            Dim several As String = If(games.Count > 1, $" ({games.Count} games)", "")
                                            addFavourite.Visible = Not games.All(Function(g) _favourites.Contains(g))
                                            removeFavourite.Visible = games.Any(Function(g) _favourites.Contains(g))
                                            addFavourite.Text = "Add to Favourites" & several
                                            removeFavourite.Text = "Remove from Favourites" & several
                                            For Each editItem As ToolStripItem In New ToolStripItem() {editSeparator, detailsItem, downloadImagesItem, downloadInfoItem, genreMenu, stopsItem, fileSeparator, renameItem, addManualItem, moveItem, deleteItem, convertItem}
                                                editItem.Visible = _editModeEnabled
                                            Next
                                            ramMenu.Visible = _editModeEnabled AndAlso ShowMemoryColumn
                                            ramMenu.Text = "RAM"
                                            renameItem.Enabled = games.Count = 1
                                            addManualItem.Enabled = games.Count = 1
                                            detailsItem.Text = "Edit Details…" & several
                                            downloadImagesItem.Text = "Download Images" & several
                                            downloadInfoItem.Text = "Download Info" & several
                                            stopsItem.Checked = games.All(Function(g) _stopsOn.Contains(g))
                                            stopsItem.Text = "Stop Tape Between Parts" & several
                                            moveItem.Text = "Move" & several
                                            deleteItem.Text = "Delete" & several
                                            Dim toConvert As Integer = games.Select(Function(g) FindGameFile(g)).
                                                Count(Function(f) f IsNot Nothing AndAlso Not IsPrgFile(f))
                                            convertItem.Enabled = toConvert > 0 AndAlso Not _converting
                                            convertItem.Text = If(toConvert > 1, $"Convert to PRG ({toConvert} games)", "Convert to PRG")
                                            If _editModeEnabled Then
                                                genreMenu.Tag = games
                                                FillRamMenu(ramMenu, games)
                                            End If
                                        End Sub

        ' A right-click on a game that is not picked picks it alone; on one that is, keeps
        ' the whole selection so the menu acts on all of it
        AddHandler ListBox1.MouseDown, Sub(sender, e)
                                           If e.Button <> MouseButtons.Right Then Return
                                           Dim index As Integer = ListBox1.IndexFromPoint(e.Location)
                                           If index >= 0 AndAlso Not ListBox1.GetSelected(index) Then
                                               ListBox1.ClearSelected()
                                               ListBox1.SelectedIndex = index
                                           End If
                                       End Sub

        ListBox1.ContextMenuStrip = contextMenu
    End Sub

    ' Convert to PRG (Edit Mode): the one program on a TAP, WAV, MP3 or other tape is
    ' saved as a PRG, which fast loads, and the original is moved to the system's
    ' Originals folder (kept out of the list). Tapes holding several programs or
    ' their own loader are left as they are, with the reason in the report.

    Private _converting As Boolean = False

    Private Shared Function IsPrgFile(gameFile As String) As Boolean
        Return String.Equals(Path.GetExtension(gameFile), ".prg", StringComparison.OrdinalIgnoreCase)
    End Function

    ' Next to the system's Games folder, or beside the chosen games folder if that is elsewhere
    Private Function OriginalsFolder() As String
        Dim beside As String = Path.Combine(SystemRoot(_currentSystem), "Originals")
        Dim games As String = Path.GetFullPath(GameFolder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Path.GetFullPath(beside).StartsWith(games, StringComparison.OrdinalIgnoreCase) Then
            ' Would be inside the games folder and show in the list: put it alongside instead
            beside = Path.GetFullPath(GameFolder).TrimEnd(Path.DirectorySeparatorChar) & " Originals"
        End If
        Return beside
    End Function

    Private Async Sub ConvertToPrg(games As List(Of String))
        If _converting Then Return
        If IsTapePlaying() Then
            MessageBox.Show("Stop the tape first.", "Convert to PRG", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim files = games.Select(Function(g) FindGameFile(g)).Where(Function(f) f IsNot Nothing AndAlso Not IsPrgFile(f)).
            Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        If files.Count = 0 Then Return
        Dim backup As String = OriginalsFolder()
        Dim what As String = If(files.Count = 1, $"'{DisplayName(games(0))}'", $"{files.Count} games")
        If MessageBox.Show($"Convert {what} to PRG?" & Environment.NewLine & Environment.NewLine &
                           "Tapes holding one program are converted, and the originals are moved to:" & Environment.NewLine &
                           backup & Environment.NewLine & Environment.NewLine &
                           "Tapes with several programs or their own loader are left as they are.",
                           "Convert to PRG", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return

        _converting = True
        Dim gamesFolder As String = GameFolder
        Dim converted As New List(Of String)
        Dim skipped As New List(Of String)
        Dim done As Integer = 0
        Await Task.Run(
            Sub()
                For Each gameFile In files
                    Dim result As String = RunConvertToPrg(gameFile, MachineFor(gameFile))
                    If result.StartsWith("Converted:") Then
                        Try
                            ' Keep the folder layout below the games folder, e.g. A-Z subfolders
                            Dim relative As String = Path.GetRelativePath(gamesFolder, gameFile)
                            If relative.StartsWith("..") OrElse Path.IsPathRooted(relative) Then relative = Path.GetFileName(gameFile)
                            Dim destination As String = Path.Combine(backup, relative)
                            Directory.CreateDirectory(Path.GetDirectoryName(destination))
                            Dim number As Integer = 2
                            While File.Exists(destination)
                                destination = Path.Combine(Path.GetDirectoryName(destination),
                                    $"{Path.GetFileNameWithoutExtension(gameFile)} ({number}){Path.GetExtension(gameFile)}")
                                number += 1
                            End While
                            File.Move(gameFile, destination)
                            converted.Add(Path.GetFileName(gameFile))
                        Catch ex As Exception
                            converted.Add($"{Path.GetFileName(gameFile)} (the original could not be moved: {ex.Message})")
                        End Try
                    Else
                        skipped.Add($"{Path.GetFileName(gameFile)}: {result.Replace("Not converted:", "").Trim()}")
                    End If
                    done += 1
                    Dim progress As String = $"Converting to PRG: {done} of {files.Count}"
                    Me.BeginInvoke(Sub() ShowActivity(progress))
                Next
            End Sub)
        _converting = False
        ShowActivity("")

        LoadGames()
        ApplyGameFilterKeeping(games)

        Dim message As String = $"{converted.Count} converted to PRG, {skipped.Count} left as they were."
        If converted.Count > 0 Then message &= Environment.NewLine & $"Originals are in {backup}"
        If skipped.Count > 0 Then
            message &= Environment.NewLine & Environment.NewLine & "Not converted:" & Environment.NewLine &
                String.Join(Environment.NewLine, skipped.Take(20))
            If skipped.Count > 20 Then message &= Environment.NewLine & $"…and {skipped.Count - 20} more"
        End If
        MessageBox.Show(message, "Convert to PRG", MessageBoxButtons.OK,
                        If(converted.Count > 0, MessageBoxIcon.Information, MessageBoxIcon.Warning))
    End Sub

    ' Runs the backend's converter on one file; returns its "Converted:" or "Not converted:" line
    Private Function RunConvertToPrg(gameFile As String, machine As String) As String
        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                .Arguments = $"""{Path.Combine(Application.StartupPath, "cbmplay.py")}"" ""{gameFile}"" --to-prg --machine {machine}",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }
            Using convertProcess As Process = Process.Start(startInfo)
                Dim errorText = convertProcess.StandardError.ReadToEndAsync()
                Dim output As String = convertProcess.StandardOutput.ReadToEnd()
                convertProcess.WaitForExit()
                Dim line = output.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).
                    LastOrDefault(Function(l) l.StartsWith("Converted:") OrElse l.StartsWith("Not converted:"))
                Return If(line, $"Not converted: {output} {errorText.Result}".Trim())
            End Using
        Catch ex As Exception
            Return "Not converted: " & ex.Message
        End Try
    End Function

    ' ---- Manuals: a button in the info window opens the game's manual, whatever its format

    Private Sub InitializeManualButton()
        _manualButton = New Button() With {.Text = "Manual", .AutoSize = True, .Enabled = False,
                                           .FlatStyle = FlatStyle.System, .TabStop = False}
        Dim tips As New ToolTip()
        tips.SetToolTip(_manualButton, "Open this game's manual. In Edit Mode, right-click a game and choose Add Manual to give it one.")
        AddHandler _manualButton.Click, AddressOf ManualButton_Click
        Me.Controls.Add(_manualButton)
        PlaceManualButton()
    End Sub

    ' In the top right corner of the info window, clear of its scroll bar
    Private Sub PlaceManualButton()
        If _manualButton Is Nothing Then Return
        _manualButton.Location = New Point(RichTextBox1.Right - _manualButton.Width - SystemInformation.VerticalScrollBarWidth - 4,
                                           RichTextBox1.Top + 3)
        _manualButton.BringToFront()
    End Sub

    Private Sub ShowManualsFor(slots As IEnumerable(Of AssetSlot), assetNames As IEnumerable(Of String))
        _currentManuals.Clear()
        For Each slot In slots
            For Each assetName As String In assetNames
                For Each candidate In ManualFilesFor(slot, assetName)
                    If Not _currentManuals.Contains(candidate, StringComparer.OrdinalIgnoreCase) Then _currentManuals.Add(candidate)
                Next
            Next
        Next
        If _manualButton IsNot Nothing Then
            _manualButton.Enabled = _currentManuals.Count > 0
            _manualButton.Text = If(_currentManuals.Count > 1, $"Manuals ({_currentManuals.Count})", "Manual")
            PlaceManualButton()
        End If
    End Sub

    ' "Game.pdf", and tagged ones such as "Game (Europe) (Quick Reference).pdf"
    Private Function ManualFilesFor(slot As AssetSlot, gameName As String) As List(Of String)
        Dim found As New List(Of String)
        For Each ext In slot.Extensions
            Dim exact As String = slot.PathFor(gameName, ext)
            If File.Exists(exact) Then found.Add(exact)
        Next
        Try
            If Directory.Exists(slot.Folder) AndAlso gameName.IndexOfAny({"*"c, "?"c}) < 0 Then
                For Each tagged In Directory.EnumerateFiles(slot.Folder, gameName & slot.Suffix & " (*")
                    If slot.Extensions.Contains(Path.GetExtension(tagged).ToLowerInvariant()) Then found.Add(tagged)
                Next
            End If
        Catch ex As Exception
            Debug.WriteLine($"Could not look for manuals: {ex.Message}")
        End Try
        Return found
    End Function

    ' Edit menu: copies the manuals in a folder (and its subfolders) to the games they
    ' belong to, matched by title. Near matches are offered to pick from.
    Private Async Sub ImportManualsFromFolder()
        Using folderDialog As New FolderBrowserDialog() With {.Description = "Folder of manuals to match to your games"}
            If folderDialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim source As String = folderDialog.SelectedPath
            Dim plainNames = _gameFiles.Values.Select(Function(f) Path.GetFileNameWithoutExtension(f)).
                Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If plainNames.Count = 0 Then Return
            Dim jobsFile As String = Path.Combine(Path.GetTempPath(), "cbm_manual_games.txt")
            Dim outFile As String = Path.Combine(Path.GetTempPath(), "cbm_manual_matches.txt")
            File.WriteAllLines(jobsFile, plainNames.Select(Function(n) _currentSystem & vbTab & n))
            If File.Exists(outFile) Then File.Delete(outFile)

            ShowActivity("Matching manuals…")
            Dim problem As String = Await Task.Run(
                Function() As String
                    Try
                        Dim startInfo As New ProcessStartInfo() With {
                            .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                            .Arguments = $"""{Path.Combine(Application.StartupPath, "cbmassets.py")}"" --jobs ""{jobsFile}"" --match-manuals ""{source}"" --out ""{outFile}""",
                            .UseShellExecute = False, .CreateNoWindow = True,
                            .RedirectStandardOutput = True, .RedirectStandardError = True}
                        Using matcher As Process = Process.Start(startInfo)
                            Dim errors = matcher.StandardError.ReadToEndAsync()
                            Dim output As String = matcher.StandardOutput.ReadToEnd()
                            matcher.WaitForExit()
                            Return If(File.Exists(outFile), "", $"{output} {errors.Result}".Trim())
                        End Using
                    Catch ex As Exception
                        Return ex.Message
                    End Try
                End Function)
            ShowActivity("")
            If problem <> "" Then
                MessageBox.Show("Could not match the manuals: " & problem, "Import Manuals", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' path, tags, exact|maybe|none, game(s)
            Dim exact As New List(Of String())
            Dim maybes As New List(Of String())
            Dim byFileName As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase)
            Dim unmatched As Integer = 0
            For Each line In File.ReadAllLines(outFile)
                Dim parts = line.Split(vbTab(0))
                If parts.Length < 3 Then Continue For
                Select Case parts(2)
                    Case "exact" : If parts.Length >= 4 Then exact.Add(parts)
                    Case "maybe"
                        Dim key As String = Path.GetFileName(parts(0))
                        If parts.Length >= 4 AndAlso Not byFileName.ContainsKey(key) Then
                            byFileName(key) = parts
                            maybes.Add({key}.Concat(parts.Skip(3)).ToArray())
                        End If
                    Case Else : unmatched += 1
                End Select
            Next

            Dim copied As Integer = 0
            Dim already As Integer = 0
            Dim failed As New List(Of String)
            Dim copyOne = Sub(manualPath As String, tags As String, gameName As String)
                              Dim target As String = ManualSlot(_currentSystem).PathFor(
                                  gameName & If(tags = "", "", " " & tags), Path.GetExtension(manualPath).ToLowerInvariant())
                              Try
                                  If File.Exists(target) Then
                                      already += 1
                                  Else
                                      Directory.CreateDirectory(Path.GetDirectoryName(target))
                                      File.Copy(manualPath, target)
                                      copied += 1
                                  End If
                              Catch ex As Exception
                                  failed.Add($"{Path.GetFileName(manualPath)}: {ex.Message}")
                              End Try
                          End Sub
            For Each row In exact
                copyOne(row(0), row(1), row(3))
            Next
            If maybes.Count > 0 Then
                Dim chosen = ChooseMaybeMatches(maybes,
                    $"{maybes.Count} manual(s) only nearly match a game. Pick the game each one belongs to, or leave it " &
                    "on ""skip for now"". Manuals that match exactly have already been added.", "Manual", "For game...", True)
                If chosen IsNot Nothing Then
                    For Each pair In chosen.Where(Function(p) p.Value <> "-")
                        Dim row = byFileName(pair.Key)
                        copyOne(row(0), row(1), pair.Value)
                    Next
                End If
            End If

            If ListBox1.SelectedItem IsNot Nothing Then LoadGameAssets(ListBox1.SelectedItem.ToString())
            Dim message As String = $"{copied} manual(s) added." &
                If(already > 0, $" {already} were already there.", "") &
                If(unmatched > 0, $" {unmatched} matched none of your games.", "")
            If failed.Count > 0 Then message &= Environment.NewLine & Environment.NewLine & String.Join(Environment.NewLine, failed.Take(10))
            MessageBox.Show(message, "Import Manuals", MessageBoxButtons.OK,
                            If(failed.Count > 0, MessageBoxIcon.Warning, MessageBoxIcon.Information))
        End Using
    End Sub

    Private Sub ManualButton_Click(sender As Object, e As EventArgs)
        If _currentManuals.Count = 0 Then Return
        If _currentManuals.Count = 1 Then
            OpenManual(_currentManuals(0))
            Return
        End If
        ' Several: pick one
        Dim pick As New ContextMenuStrip()
        For Each manualPath In _currentManuals
            Dim chosen As String = manualPath
            Dim kind As String = Path.GetExtension(chosen).TrimStart("."c).ToUpperInvariant()
            Dim item As New ToolStripMenuItem($"{kind}: {Path.GetFileName(chosen)}")
            AddHandler item.Click, Sub(s, args) OpenManual(chosen)
            pick.Items.Add(item)
        Next
        pick.Show(_manualButton, New Point(0, _manualButton.Height))
    End Sub

    ' Text and pictures open in a window of the app's own, and so do PDFs and web pages
    ' (shown by WebView2, Edge's viewer inside the app). Anything else, or a PDF on a PC
    ' without WebView2, opens in the program Windows uses for that kind of file.
    Private Shared ReadOnly _webManualExtensions As String() = {".pdf", ".htm", ".html"}
    Private Sub OpenManual(manualPath As String)
        If Not File.Exists(manualPath) Then
            MessageBox.Show("That manual is no longer there.", "Manual", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim ext As String = Path.GetExtension(manualPath).ToLowerInvariant()
        Dim title As String = $"Manual: {If(ListBox1.SelectedItem IsNot Nothing, DisplayName(ListBox1.SelectedItem.ToString()), Path.GetFileNameWithoutExtension(manualPath))}"
        Try
            If ext = ".txt" Then
                ShowTextManual(manualPath, title)
            ElseIf _pictureManualExtensions.Contains(ext) Then
                ShowPictureManual(manualPath, title)
            ElseIf _webManualExtensions.Contains(ext) Then
                ShowWebManual(manualPath, title)
            Else
                Process.Start(New ProcessStartInfo(manualPath) With {.UseShellExecute = True})
            End If
        Catch ex As System.ComponentModel.Win32Exception
            MessageBox.Show($"There is no program on this computer set to open {ext} files." & Environment.NewLine & manualPath,
                            "Manual", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show($"Could not open the manual: {ex.Message}", "Manual", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Sub ShowTextManual(manualPath As String, title As String)
        Dim viewer As New Form() With {.Text = title, .Icon = Me.Icon, .Size = New Size(720, 640),
                                       .StartPosition = FormStartPosition.CenterParent, .ShowInTaskbar = True}
        Dim box As New RichTextBox() With {.Dock = DockStyle.Fill, .ReadOnly = True, .BackColor = SystemColors.Window,
                                           .Font = New Font("Consolas", 10.0F), .WordWrap = True, .DetectUrls = True,
                                           .Text = File.ReadAllText(manualPath)}
        viewer.Controls.Add(box)
        viewer.Show(Me)        ' not modal, so the game list stays usable alongside it
    End Sub

    Private Sub ShowWebManual(manualPath As String, title As String)
        Try
            OpenInWebView(manualPath, title)
        Catch ex As Exception
            ' The WebView2 files are not with the app: use the PC's own viewer
            OpenWithWindows(manualPath)
        End Try
    End Sub

    ' WebView2 is loaded when it is needed, so the app builds and runs without it. Its
    ' files come from the Microsoft.Web.WebView2 NuGet package (copied beside the exe),
    ' and it needs the WebView2 runtime, which Windows 10 and 11 have.
    Private Async Sub OpenInWebView(manualPath As String, title As String)
        Dim webType As Type
        Dim environmentType As Type
        Try
            Dim winForms = Reflection.Assembly.Load("Microsoft.Web.WebView2.WinForms")
            Dim core = Reflection.Assembly.Load("Microsoft.Web.WebView2.Core")
            webType = winForms.GetType("Microsoft.Web.WebView2.WinForms.WebView2", True)
            environmentType = core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", True)
        Catch ex As Exception
            ' The WebView2 files are not with the app: use the PC's own viewer
            OpenWithWindows(manualPath)
            Return
        End Try

        Dim screenArea As Rectangle = Screen.FromControl(Me).WorkingArea
        Dim viewer As New Form() With {
            .Text = title, .Icon = Me.Icon, .StartPosition = FormStartPosition.CenterParent, .ShowInTaskbar = True,
            .Size = New Size(Math.Min(900, screenArea.Width - 40), screenArea.Height - 40)}
        Dim web As Control
        Try
            web = CType(Activator.CreateInstance(webType), Control)
        Catch ex As Exception
            viewer.Dispose()
            OpenWithWindows(manualPath)
            Return
        End Try
        web.Dock = DockStyle.Fill
        viewer.Controls.Add(web)
        viewer.Show(Me)
        Try
            ' Its working files go in the user's own app data, not beside the app
            Dim dataFolder As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                    "CommodoreGameLoader", "WebView2")
            Dim create = environmentType.GetMethods().First(Function(m) m.Name = "CreateAsync" AndAlso m.GetParameters().Length = 3)
            Dim creating As Task = CType(create.Invoke(Nothing, New Object() {Nothing, dataFolder, Nothing}), Task)
            Await creating
            Dim webEnvironment As Object = creating.GetType().GetProperty("Result").GetValue(creating)

            Dim ensure = webType.GetMethods().First(Function(m) m.Name = "EnsureCoreWebView2Async" AndAlso m.GetParameters().Length = 1)
            Await CType(ensure.Invoke(web, New Object() {webEnvironment}), Task)
            webType.GetProperty("Source").SetValue(web, New Uri(manualPath))
        Catch ex As Exception
            ' No WebView2 runtime on this PC (older Windows): use the PC's own viewer
            viewer.Close()
            OpenWithWindows(manualPath)
        End Try
    End Sub

    Private Sub OpenWithWindows(manualPath As String)
        Try
            Process.Start(New ProcessStartInfo(manualPath) With {.UseShellExecute = True})
        Catch ex As Exception
            MessageBox.Show($"There is no program on this computer set to open {Path.GetExtension(manualPath)} files." &
                            Environment.NewLine & manualPath, "Manual", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Try
    End Sub

    Private Sub ShowPictureManual(manualPath As String, title As String)
        ' Read into memory so the file is not kept locked while the window is open
        Dim picture As Image
        Using stream As New MemoryStream(File.ReadAllBytes(manualPath))
            picture = New Bitmap(Image.FromStream(stream))
        End Using
        Dim viewer As New Form() With {.Text = title, .Icon = Me.Icon, .StartPosition = FormStartPosition.CenterParent,
                                       .ShowInTaskbar = True, .BackColor = Color.Black}
        Dim screenArea As Rectangle = Screen.FromControl(Me).WorkingArea
        Dim scale As Double = Math.Min(1.0, Math.Min((screenArea.Width - 80) / picture.Width, (screenArea.Height - 80) / picture.Height))
        viewer.ClientSize = New Size(Math.Max(320, CInt(picture.Width * scale)), Math.Max(240, CInt(picture.Height * scale)))
        Dim box As New PictureBox() With {.Dock = DockStyle.Fill, .SizeMode = PictureBoxSizeMode.Zoom, .Image = picture}
        viewer.Controls.Add(box)
        AddHandler viewer.FormClosed, Sub(s, args) picture.Dispose()
        viewer.Show(Me)
    End Sub

    ' Edit Mode: copies a manual file you have into the Manuals folder under the game's name
    Private Sub AddManual(games As List(Of String))
        If games.Count <> 1 Then Return
        Dim plainName As String = PlainNameOf(games(0))
        Using picker As New OpenFileDialog() With {
            .Title = $"Manual for {DisplayName(games(0))}",
            .Filter = "Manuals|" & String.Join(";", _allowedManualExtensions.Select(Function(x) "*" & x)) & "|All files|*.*"}
            If picker.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim ext As String = Path.GetExtension(picker.FileName).ToLowerInvariant()
            If Not _allowedManualExtensions.Contains(ext) Then
                MessageBox.Show($"{ext} files can't be used as manuals. Supported: " & String.Join(" ", _allowedManualExtensions),
                                "Add Manual", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            Dim target As String = ManualSlot(_currentSystem).PathFor(plainName, ext)
            If File.Exists(target) AndAlso MessageBox.Show($"{Path.GetFileName(target)} is already there. Replace it?", "Add Manual",
                                                           MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
            Try
                SaveShownText()
                Directory.CreateDirectory(Path.GetDirectoryName(target))
                File.Copy(picker.FileName, target, True)
            Catch ex As Exception
                MessageBox.Show($"Could not add the manual: {ex.Message}", "Add Manual", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try
            If ListBox1.SelectedItem IsNot Nothing Then LoadGameAssets(ListBox1.SelectedItem.ToString())
        End Using
    End Sub

    ' Download Images or Download Info from the right-click menu, for the chosen games only
    Private Sub DownloadForGames(games As List(Of String), imageKinds As String, includeInfo As Boolean)
        Dim files As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each game In games
            Dim gameFile As String = FindGameFile(game)
            If gameFile IsNot Nothing Then files.Add(gameFile)
        Next
        If files.Count = 0 Then Return
        StartAssetDownload(imageKinds, includeInfo, files, forceRetry:=True, sayIfNothing:=True)
    End Sub

    Private Sub SetTapeStops(games As List(Of String), stopsOn As Boolean)
        For Each game In games
            If stopsOn Then _stopsOn.Add(game) Else _stopsOn.Remove(game)
        Next
        SaveFastLoadSettings()
    End Sub

    Private Sub SetFavourites(games As List(Of String), isFavourite As Boolean)
        For Each game In games
            If isFavourite Then _favourites.Add(game) Else _favourites.Remove(game)
        Next
        SaveFastLoadSettings()
        If _viewCombo IsNot Nothing AndAlso TypeOf _viewCombo.SelectedItem Is String AndAlso _viewCombo.SelectedItem.ToString() = VIEW_FAVOURITES Then
            ApplyGameFilter()
        Else
            ListBox1.Invalidate()
        End If
    End Sub

    ' Every genre known, ticked where all the picked games have it; then no genre, and a new one
    Private Sub FillGenreMenu(menu As ToolStripMenuItem, games As List(Of String))
        menu.DropDownItems.Clear()
        Dim haveGenres = games.Select(Function(g) GenresOf(DetailsFor(g)).ToList()).ToList()
        For Each genre In KnownGenres()
            Dim genreName As String = genre
            Dim item As New ToolStripMenuItem(genreName) With {
                .Checked = haveGenres.All(Function(list) list.Contains(genreName, StringComparer.OrdinalIgnoreCase))}
            AddHandler item.Click, Sub(sender, e) SetGenres(games, genreName)
            menu.DropDownItems.Add(item)
        Next
        menu.DropDownItems.Add(New ToolStripSeparator())
        Dim noGenre As New ToolStripMenuItem("No Genre") With {.Checked = haveGenres.All(Function(list) list.Count = 0)}
        AddHandler noGenre.Click, Sub(sender, e) SetGenres(games, "")
        menu.DropDownItems.Add(noGenre)
        Dim newGenre As New ToolStripMenuItem("New Genre…")
        AddHandler newGenre.Click, Sub(sender, e)
                                       Dim typed As String = InputBox("Name of the new genre. Separate several with commas.", "New Genre")
                                       If typed.Trim() <> "" Then SetGenres(games, typed)
                                   End Sub
        menu.DropDownItems.Add(newGenre)
    End Sub

    Private Sub FillRamMenu(menu As ToolStripMenuItem, games As List(Of String))
        menu.DropDownItems.Clear()
        For Each sizeName In RamSizesForSystem()
            Dim choice As String = sizeName
            Dim item As New ToolStripMenuItem(choice) With {
                .Checked = games.All(Function(g) String.Equals(MemoryFor(g), choice, StringComparison.OrdinalIgnoreCase))}
            AddHandler item.Click, Sub(sender, e) SetInfoFieldForGames(games, "Memory", choice)
            menu.DropDownItems.Add(item)
        Next
        menu.DropDownItems.Add(New ToolStripSeparator())
        Dim clearItem As New ToolStripMenuItem(If(ScanFindsMemory, "As Found in the File", "Not Set"))
        AddHandler clearItem.Click, Sub(sender, e) SetInfoFieldForGames(games, "Memory", "")
        menu.DropDownItems.Add(clearItem)
    End Sub

    Private Sub SetGenres(games As List(Of String), typed As String)
        Dim genres As String = String.Join(", ", typed.Split(","c).Select(Function(g) CanonicalGenre(g)).
            Where(Function(g) g <> "").Distinct(StringComparer.OrdinalIgnoreCase))
        SetInfoFieldForGames(games, "Genre", genres)
    End Sub

    ' Writes one info line for each game, then shows the changes
    Private Sub SetInfoFieldForGames(games As List(Of String), field As String, value As String)
        SaveShownText()
        Dim failed As New List(Of String)
        For Each game In games
            Try
                WriteInfoFields(PlainNameOf(game), New Dictionary(Of String, String) From {{field, value}})
            Catch ex As Exception
                failed.Add($"{DisplayName(game)}: {ex.Message}")
            End Try
        Next
        AfterDetailsChanged()
        If failed.Count > 0 Then
            MessageBox.Show("Could not save:" & Environment.NewLine & String.Join(Environment.NewLine, failed.Take(10)),
                            "Edit", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End If
    End Sub

    Private Function PlainNameOf(label As String) As String
        Dim gameFile As String = Nothing
        Return If(_gameFiles.TryGetValue(label, gameFile), Path.GetFileNameWithoutExtension(gameFile), label)
    End Function

    ' Redraws the list and the shown game's text after info files were changed
    Private Sub AfterDetailsChanged()
        Dim picked = SelectedGames()
        RefreshDetails()
        ApplyGameFilterKeeping(picked)
        If ListBox1.SelectedItem IsNot Nothing Then LoadGameAssets(ListBox1.SelectedItem.ToString())
    End Sub

    ' Refilters the list (a game may have left the view) and picks the same games again
    Private Sub ApplyGameFilterKeeping(picked As List(Of String))
        ApplyGameFilter()
        If picked.Count = 0 Then Return
        ListBox1.ClearSelected()
        For Each game In picked
            Dim index As Integer = ListBox1.Items.IndexOf(game)
            If index >= 0 Then
                If ListBox1.SelectionMode = SelectionMode.One Then
                    ListBox1.SelectedIndex = index
                    Exit For
                End If
                ListBox1.SetSelected(index, True)
            End If
        Next
        If ListBox1.SelectedIndex < 0 AndAlso ListBox1.Items.Count > 0 Then ListBox1.SelectedIndex = 0
    End Sub

    ' Edit Mode: title, RAM, year, publisher, developer and genre for one game or several.
    ' For several, a box left empty keeps each game's own value.
    Private Sub EditDetails(games As List(Of String))
        If games.Count = 0 Then Return
        SaveShownText()
        Dim oneGame As Boolean = games.Count = 1
        Dim plain As String = PlainNameOf(games(0))
        Dim showRam As Boolean = ShowMemoryColumn

        ' What the boxes start with, to tell what was changed
        Dim startTitle As String = If(oneGame, DisplayName(games(0)), "")
        Dim start As New Dictionary(Of String, String) From {
            {"Year", If(oneGame, DetailsFor(games(0)).Year, "")},
            {"Publisher", If(oneGame, InfoValue(plain, "Publisher"), "")},
            {"Developer", If(oneGame, InfoValue(plain, "Developer"), "")},
            {"Genre", If(oneGame, String.Join(", ", GenresOf(DetailsFor(games(0)))), "")},
            {"Memory", If(oneGame, InfoValue(plain, "Memory"), "")}}
        If oneGame AndAlso start("Publisher") = "" Then start("Publisher") = DetailsFor(games(0)).Publisher

        Using dialog As New Form() With {
            .Text = If(oneGame, "Edit Details", $"Edit Details ({games.Count} games)"),
            .Icon = Me.Icon, .FormBorderStyle = FormBorderStyle.FixedDialog, .MaximizeBox = False, .MinimizeBox = False,
            .StartPosition = FormStartPosition.CenterParent, .ShowInTaskbar = False, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Font = Me.Font}

            Dim table As New TableLayoutPanel() With {.ColumnCount = 2, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Padding = New Padding(12)}
            table.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            table.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 300))
            Dim addRow = Sub(caption As String, box As Control)
                             table.Controls.Add(New Label() With {.Text = caption, .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(0, 6, 8, 6)})
                             box.Dock = DockStyle.Fill
                             box.Margin = New Padding(0, 3, 0, 3)
                             table.Controls.Add(box)
                         End Sub

            If Not oneGame Then
                Dim note As New Label() With {.AutoSize = True, .MaximumSize = New Size(420, 0), .ForeColor = Color.DimGray, .Margin = New Padding(0, 0, 0, 8),
                    .Text = $"Changing {games.Count} games. A box left empty keeps each game's own value."}
                table.Controls.Add(note)
                table.SetColumnSpan(note, 2)
            End If
            Dim titleBox As New TextBox() With {.Text = startTitle}
            If oneGame Then addRow("Title:", titleBox)
            Dim ramBox As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDown, .Text = start("Memory")}
            ramBox.Items.AddRange(RamSizesForSystem().Cast(Of Object)().ToArray())
            If showRam Then addRow("RAM:", ramBox)
            Dim yearBox As New TextBox() With {.Text = start("Year"), .MaxLength = 10}
            addRow("Year:", yearBox)
            Dim publisherBox As New TextBox() With {.Text = start("Publisher")}
            addRow("Publisher:", publisherBox)
            Dim developerBox As New TextBox() With {.Text = start("Developer")}
            addRow("Developer:", developerBox)
            ' Always open to choose from: pick a genre the other games have, or type a new one
            Dim genreBox As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDown, .Text = start("Genre"),
                                                 .AutoCompleteMode = AutoCompleteMode.SuggestAppend, .AutoCompleteSource = AutoCompleteSource.ListItems}
            genreBox.Items.AddRange(KnownGenres().Cast(Of Object)().ToArray())
            addRow("Genre:", genreBox)
            Dim hint As New Label() With {.AutoSize = True, .ForeColor = Color.DimGray, .Margin = New Padding(0, 0, 0, 6),
                                          .Text = "Pick a genre or type your own; separate several with commas."}
            table.Controls.Add(New Label() With {.AutoSize = True})
            table.Controls.Add(hint)

            Dim buttons As New FlowLayoutPanel() With {.FlowDirection = FlowDirection.RightToLeft, .AutoSize = True, .Dock = DockStyle.Fill, .Margin = New Padding(0, 8, 0, 0)}
            Dim cancelChoice As New Button() With {.Text = "Cancel", .DialogResult = DialogResult.Cancel, .AutoSize = True}
            Dim saveChoice As New Button() With {.Text = "Save", .DialogResult = DialogResult.OK, .AutoSize = True}
            buttons.Controls.Add(cancelChoice)
            buttons.Controls.Add(saveChoice)
            table.Controls.Add(buttons)
            table.SetColumnSpan(buttons, 2)
            dialog.Controls.Add(table)
            dialog.AcceptButton = saveChoice
            dialog.CancelButton = cancelChoice
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return

            ' Only what was changed is written
            Dim typed As New Dictionary(Of String, String) From {
                {"Year", yearBox.Text.Trim()}, {"Publisher", publisherBox.Text.Trim()},
                {"Developer", developerBox.Text.Trim()},
                {"Genre", String.Join(", ", genreBox.Text.Split(","c).Select(Function(g) CanonicalGenre(g)).
                    Where(Function(g) g <> "").Distinct(StringComparer.OrdinalIgnoreCase))}}
            If showRam Then typed("Memory") = ramBox.Text.Trim()
            Dim fields As New Dictionary(Of String, String)
            For Each pair In typed
                If oneGame Then
                    If pair.Value <> start(pair.Key) Then fields(pair.Key) = pair.Value
                ElseIf pair.Value <> "" Then
                    fields(pair.Key) = pair.Value
                End If
            Next

            If oneGame Then
                Dim newTitle As String = titleBox.Text.Trim()
                If newTitle <> startTitle Then
                    Dim plainTitle As String = CleanTitle(plain)
                    If newTitle = "" OrElse newTitle = plainTitle Then
                        _titleOverrides.Remove(games(0))
                    Else
                        _titleOverrides(games(0)) = newTitle
                    End If
                    SaveTitleOverrides()
                    If newTitle <> "" Then fields("Title") = newTitle
                End If
            End If

            Dim failed As New List(Of String)
            If fields.Count > 0 Then
                For Each game In games
                    Try
                        WriteInfoFields(PlainNameOf(game), fields)
                    Catch ex As Exception
                        failed.Add($"{DisplayName(game)}: {ex.Message}")
                    End Try
                Next
            End If
            AfterDetailsChanged()
            If failed.Count > 0 Then
                MessageBox.Show("Could not save:" & Environment.NewLine & String.Join(Environment.NewLine, failed.Take(10)),
                                "Edit Details", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If
        End Using
    End Sub

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Me.StartPosition = FormStartPosition.Manual
        Me.Location = New Point(Screen.PrimaryScreen.WorkingArea.Left, Screen.PrimaryScreen.WorkingArea.Top)
        Me.Text = SystemTitle()
        Me.Icon = New Icon(Me.GetType(), "Icon.ico")
        AddHandler TimerResetStatus.Tick, AddressOf ResetStatusMessage

        ' Initialize with Edit Mode OFF by default
        ToggleEditMode(False)

        RunDeleteTempFiles()
        ResetBlockLabels()
        LoadTapeOptions()
        Me.Text = SystemTitle()
        MoveToSystemLayout()
        EnsureSystemFolders()
        LoadFastScanResults()
        InitializeMenuStrip()
        BuildStatusPanel()
        InitializeStatusLines()
        AddFastLoadControls()
        SetUpGameList()
        InitializeResizing()
        LoadSettings()
        InitializeContextMenu()
        InitializeManualButton()

        ' Initialize the timer (but don't start it yet)
        tapeCounterTimer = New Timer() With {.Interval = 250}
        AddHandler tapeCounterTimer.Tick, AddressOf UpdateTapeCounter

        ' Load games first
        LoadGames()

        ' Then try to restore last selected game if setting is enabled
        RestoreLastGame()
    End Sub

    Private Sub InitializeMenuStrip()
        Dim menuStrip As New MenuStrip()

        ' File menu
        Dim fileMenuItem As New ToolStripMenuItem("File")
        Dim exitMenuItem As New ToolStripMenuItem("Exit")
        AddHandler exitMenuItem.Click, Sub(sender, e) Me.Close()
        fileMenuItem.DropDownItems.Add(exitMenuItem)
        menuStrip.Items.Add(fileMenuItem)

        ' Edit Mode menu
        Dim editModeMenuItem As New ToolStripMenuItem("Edit Mode")
        _editModeMenuItem = New ToolStripMenuItem("Editor Off")
        AddHandler _editModeMenuItem.Click, AddressOf EditModeToolStripMenuItem_Click
        editModeMenuItem.DropDownItems.Add(_editModeMenuItem)
        AddDownloadMenuItems(editModeMenuItem)
        menuStrip.Items.Add(editModeMenuItem)

        ' System menu
        menuStrip.Items.Add(BuildSystemMenu())

        ' Settings menu
        Dim settingsMenuItem As New ToolStripMenuItem("Settings")
        AddHandler settingsMenuItem.Click, AddressOf OpenSettingsForm
        menuStrip.Items.Add(settingsMenuItem)

        ' Tape options menu
        menuStrip.Items.Add(BuildViewMenu())

        ' Help menu
        Dim helpMenuItem As New ToolStripMenuItem("Help")
        AddHandler helpMenuItem.Click, AddressOf ShowFormattedHelp
        menuStrip.Items.Add(helpMenuItem)

        ' About menu
        Dim aboutMenuItem As New ToolStripMenuItem("About")
        AddHandler aboutMenuItem.Click, AddressOf OpenAboutForm
        menuStrip.Items.Add(aboutMenuItem)

        ' Scanning and downloading messages, on the far right
        _activityLabel = New ToolStripLabel("") With {.Alignment = ToolStripItemAlignment.Right, .ForeColor = Color.DimGray}
        menuStrip.Items.Add(_activityLabel)

        Me.Controls.Add(menuStrip)
        menuStrip.Dock = DockStyle.Top
    End Sub

    Private Sub ResetStatusMessage(sender As Object, e As EventArgs)
        TimerResetStatus.Stop()
        LblTapeStatus.Text = "Ready"
    End Sub

    Private Sub OpenSettingsForm(sender As Object, e As EventArgs)
        Dim settingsForm As New SettingsForm() With {
            .InvertSignal = _invertOption,
            .SineWave = _sineOption,
            .LeaderPercent = _leaderOption,
            .SettingsFilePath = _settingsFilePath,
            .SystemName = _systemNames(_currentSystem),
            .SystemFolder = SystemRoot(_currentSystem)
        }
        settingsForm.ShowDialog()
        If settingsForm.Saved Then
            _invertOption = settingsForm.InvertSignal
            _sineOption = settingsForm.SineWave
            _leaderOption = settingsForm.LeaderPercent
            SaveTapeOptions()
        End If
        LoadSettings()
        LoadGames()
    End Sub

    Private Sub ShowFormattedHelp(sender As Object, e As EventArgs)
        Dim helpForm As New HelpForm()
        helpForm.ShowDialog()
    End Sub

    Private Sub OpenAboutForm(sender As Object, e As EventArgs)
        Dim helpForm As New AboutForm()
        AboutForm.ShowDialog()
    End Sub

    Private Sub LoadSettings()
        Dim folders As String() = SystemFolders(_currentSystem)
        LoadStartupCaches()
        GameFolder = folders(0)
        ImageFolder = folders(1)
        ManualFolder = folders(2)
        _infoFolder = folders(3)

        ' Chosen folders already exist; this only makes the system's own ones if missing
        For Each folder In folders
            Try
                Directory.CreateDirectory(folder)
            Catch ex As Exception
                Debug.WriteLine($"Could not create {folder}: {ex.Message}")
            End Try
        Next

        LoadFastLoadSettings()

        If _gameFolderWatcher Is Nothing Then
            InitializeGameFolderWatcher()
        Else
            _gameFolderWatcher.Path = GameFolder
        End If

    End Sub

    Private Sub LoadGames()
        If Not Directory.Exists(GameFolder) Then
            MessageBox.Show("Game folder not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End If

        ' Store the current selection if it exists
        Dim currentSelection As String = If(ListBox1.SelectedItem?.ToString(), String.Empty)
        Dim currentIndex As Integer = ListBox1.SelectedIndex

        RebuildGameIndex()
        LoadGameDetails()
        RefreshViewChoices()
        StartFastLoadScan()
        _allGames.Clear()
        _allGames.AddRange(_gameFiles.Keys)
        _allGames.Sort()

        ApplyGameFilter()

        ' Try to maintain selection if possible
        If Not String.IsNullOrEmpty(currentSelection) Then
            Dim newIndex = ListBox1.Items.IndexOf(currentSelection)
            If newIndex >= 0 Then
                ListBox1.SelectedIndex = newIndex
                ' Force load the assets for the selected game
                _lastSelectedGame = "" ' Reset to force reload
                ListBox1_SelectedIndexChanged(ListBox1, EventArgs.Empty)
                Return
            End If
        End If

        If ListBox1.Items.Count > 0 Then
            ' If we had a previous selection that's no longer available,
            ' try to select the item at the same index if possible
            If currentIndex >= 0 AndAlso currentIndex < ListBox1.Items.Count Then
                ListBox1.SelectedIndex = currentIndex
            Else
                ListBox1.SelectedIndex = 0
            End If
            ' Force load the assets for the selected game
            _lastSelectedGame = "" ' Reset to force reload
            ListBox1_SelectedIndexChanged(ListBox1, EventArgs.Empty)
        End If
    End Sub

    Private Sub ApplyGameFilter()
        ListBox1.BeginUpdate()
        ListBox1.Items.Clear()

        Dim shown = _allGames.Where(Function(game) InCurrentView(game))
        If Not String.IsNullOrWhiteSpace(_currentSearchTerm) Then
            Dim searchTerms = _currentSearchTerm.ToLowerInvariant().Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            shown = shown.Where(Function(game)
                                    Dim searchText As String = SearchTextFor(game)
                                    Return searchTerms.All(Function(term) searchText.Contains(term))
                                End Function)
        End If
        ' In order of the name shown, not the file name: "3d_pool", "3D Pool (1989)(Firebird)"
        ' and "3D_Pool [cr]" all show as 3D Pool, so they belong together. Games with
        ' the same shown name keep a fixed order, by file name.
        shown = shown.OrderBy(Function(game) DisplayName(game), StringComparer.CurrentCultureIgnoreCase).
                      ThenBy(Function(game) game, StringComparer.OrdinalIgnoreCase)
        If ListBox1.Sorted Then ListBox1.Sorted = False   ' the list's own sorting would put file-name order back
        ListBox1.Items.AddRange(shown.ToArray())

        If ListBox1.Items.Count > 0 Then ListBox1.SelectedIndex = 0
        ListBox1.EndUpdate()
        Me.Text = SystemTitle()
    End Sub

    Private Sub RenameGame(sender As Object, e As EventArgs)
        If Not _editModeEnabled OrElse ListBox1.SelectedIndex = -1 Then Return

        Dim oldName As String = ListBox1.SelectedItem.ToString()
        Dim oldGamePath As String = FindGameFile(oldName)
        If oldGamePath Is Nothing Then
            MessageBox.Show("Original game file not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End If

        Dim oldFileName As String = Path.GetFileNameWithoutExtension(oldGamePath)
        Dim newName As String = InputBox("Enter new name for the game:", "Rename Game", oldFileName)

        If String.IsNullOrWhiteSpace(newName) OrElse newName = oldFileName Then Return

        Try
            Dim gameDirectory As String = Path.GetDirectoryName(oldGamePath)
            Dim newGamePath As String = Path.Combine(gameDirectory, newName & Path.GetExtension(oldGamePath))

            If Not SafeRenameFile(oldGamePath, newGamePath) Then
                MessageBox.Show("Failed to rename game file", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
                Return
            End If

            Dim gameSystemName As String = GameSystem(newGamePath)
            MoveAssets(oldFileName, gameSystemName, newName, gameSystemName)
            If oldName <> oldFileName Then MoveAssets(oldName, gameSystemName, newName, gameSystemName)

            Dim savedType As Integer
            If _fastLoadTypes.TryGetValue(oldName, savedType) Then
                _fastLoadTypes.Remove(oldName)
                _fastLoadTypes(newName) = savedType
            End If
            If _favourites.Remove(oldName) Then _favourites.Add(newName)
            If _stopsOn.Remove(oldName) Then _stopsOn.Add(newName)
            SaveFastLoadSettings()

            LoadGames()

            Dim newIndex As Integer = ListBox1.Items.IndexOf(newName)
            If newIndex >= 0 Then
                ListBox1.SelectedIndex = newIndex
            End If

            MessageBox.Show("Game renamed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show($"Error renaming game: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub MoveGame(sender As Object, e As EventArgs)
        If Not _editModeEnabled Then Return
        Dim games = SelectedGames()
        If games.Count = 0 Then Return

        Using folderDialog As New FolderBrowserDialog()
            folderDialog.Description = If(games.Count = 1, "Select destination folder for the game",
                                          $"Select destination folder for the {games.Count} games")
            folderDialog.SelectedPath = GameFolder
            If folderDialog.ShowDialog() <> DialogResult.OK Then Return
            Dim destination As String = folderDialog.SelectedPath

            Dim moved As Integer = 0
            Dim alreadyThere As Integer = 0
            Dim failed As New List(Of String)
            Try
                Directory.CreateDirectory(destination)
            Catch ex As Exception
                MessageBox.Show($"Could not use that folder: {ex.Message}", "Move", MessageBoxButtons.OK, MessageBoxIcon.Error)
                Return
            End Try
            PrepareForFileOperation()
            ' Tagged entries can share one file: move each file once
            For Each oldGamePath In games.Select(Function(g) FindGameFile(g)).Where(Function(f) f IsNot Nothing).
                    Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                Try
                    If String.Equals(Path.GetDirectoryName(oldGamePath), destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) Then
                        alreadyThere += 1
                        Continue For
                    End If
                    Dim newGamePath As String = Path.Combine(destination, Path.GetFileName(oldGamePath))
                    Dim oldSystem As String = GameSystem(oldGamePath)
                    If Not SafeRenameFile(oldGamePath, newGamePath) Then
                        failed.Add(Path.GetFileName(oldGamePath))
                        Continue For
                    End If
                    ' Moving to another system's folder takes its images and info along
                    Dim newSystem As String = If(SystemOwningFolder(destination), _currentSystem)
                    If newSystem <> oldSystem Then
                        Dim plainName As String = Path.GetFileNameWithoutExtension(newGamePath)
                        MoveAssets(plainName, oldSystem, plainName, newSystem)
                    End If
                    moved += 1
                Catch ex As Exception
                    failed.Add($"{Path.GetFileName(oldGamePath)}: {ex.Message}")
                End Try
            Next

            LoadGames()
            ApplyGameFilterKeeping(games)

            Dim message As String = If(moved = 1, "Game moved.", $"{moved} games moved.")
            If alreadyThere > 0 Then message &= Environment.NewLine & $"{alreadyThere} already in that folder."
            If failed.Count > 0 Then
                message &= Environment.NewLine & Environment.NewLine & "Could not move:" & Environment.NewLine & String.Join(Environment.NewLine, failed.Take(10))
            End If
            MessageBox.Show(message, "Move", MessageBoxButtons.OK, If(failed.Count > 0, MessageBoxIcon.Warning, MessageBoxIcon.Information))
        End Using
    End Sub

    Private Sub DeleteGame(sender As Object, e As EventArgs)
        If Not _editModeEnabled Then Return
        Dim games = SelectedGames()
        If games.Count = 0 Then Return
        Dim firstIndex As Integer = ListBox1.SelectedIndex

        Dim what As String = If(games.Count = 1, $"'{DisplayName(games(0))}'", $"these {games.Count} games")
        Dim result As DialogResult = MessageBox.Show(
            $"Are you sure you want to delete {what}?" & Environment.NewLine &
            "The game files and their images, info and manuals go to the Recycle Bin.",
            "Confirm Deletion", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
        If result <> DialogResult.OK Then Return

        ' Every file is looked up before anything goes. The folder watcher would otherwise
        ' reload the list after the first one, and copies of the same game are labelled
        ' again ("3D Pool [TAP]" becomes "3D Pool" once it is the only one left), so a
        ' later one could no longer be found and was quietly left behind.
        Dim targets = games.Select(Function(g) New KeyValuePair(Of String, String)(g, FindGameFile(g))).ToList()
        Dim going = targets.Where(Function(t) t.Value IsNot Nothing).Select(Function(t) t.Value).
            ToHashSet(StringComparer.OrdinalIgnoreCase)
        Dim keepNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each entry In _gameFiles
            If Not going.Contains(entry.Value) Then
                keepNames.Add(entry.Key)
                keepNames.Add(Path.GetFileNameWithoutExtension(entry.Value))
            End If
        Next
        Dim watching As Boolean = _gameFolderWatcher IsNot Nothing AndAlso _gameFolderWatcher.EnableRaisingEvents
        If watching Then _gameFolderWatcher.EnableRaisingEvents = False
        Try

        PrepareForFileOperation()
        Dim failed As New List(Of String)
        Dim settingsChanged As Boolean = False
        Dim titlesChanged As Boolean = False
        Dim doneFiles As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each target In targets
            Dim gameName As String = target.Key
            Dim gamePath As String = target.Value
            Try
                If gamePath Is Nothing Then
                    failed.Add($"{DisplayName(gameName)}: the file was not found")
                ElseIf doneFiles.Add(gamePath) Then
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(gamePath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
                    ' Images, manual and info for the game, in every folder that can hold
                    ' them, but not ones a game that stays still uses
                    Dim gameSystemName As String = GameSystem(gamePath)
                    Dim plainName As String = Path.GetFileNameWithoutExtension(gamePath)
                    RecycleAssets(plainName, gameSystemName, keepNames)
                    If gameName <> plainName Then RecycleAssets(gameName, gameSystemName, keepNames)
                End If
                If _fastLoadTypes.Remove(gameName) Then settingsChanged = True
                If _favourites.Remove(gameName) Then settingsChanged = True
                If _stopsOn.Remove(gameName) Then settingsChanged = True
                If _titleOverrides.Remove(gameName) Then titlesChanged = True
            Catch ex As Exception
                failed.Add($"{DisplayName(gameName)}: {ex.Message}")
            End Try
        Next
        If settingsChanged Then SaveFastLoadSettings()
        If titlesChanged Then SaveTitleOverrides()

        LoadGames()
        ' Pick the game that now sits where the first deleted one was
        If ListBox1.Items.Count > 0 Then
            ListBox1.ClearSelected()
            ListBox1.SelectedIndex = Math.Max(0, Math.Min(firstIndex, ListBox1.Items.Count - 1))
        End If

        If failed.Count > 0 Then
            MessageBox.Show("Could not delete:" & Environment.NewLine & String.Join(Environment.NewLine, failed.Take(10)),
                            "Delete", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End If
        Finally
            If watching Then _gameFolderWatcher.EnableRaisingEvents = True
        End Try
    End Sub

    Private Sub TerminateAllChildProcesses()
        tapeCounterTimer.Stop()

        For Each proc In _childProcesses.ToList()
            Try
                If proc IsNot Nothing Then
                    Try
                        If Not proc.HasExited Then
                            proc.Kill()
                        End If
                    Catch ex As InvalidOperationException
                    Catch ex As NotSupportedException
                    Catch ex As Exception
                        Debug.WriteLine($"Error stopping process: {ex.Message}")
                    End Try
                    proc.Dispose()
                End If
            Catch ex As Exception
                Debug.WriteLine($"Error disposing process: {ex.Message}")
            Finally
                If proc IsNot Nothing AndAlso _childProcesses.Contains(proc) Then
                    _childProcesses.Remove(proc)
                End If
            End Try
        Next
        _childProcesses.Clear()

        If tapePlayProcess IsNot Nothing Then
            Try
                Try
                    If Not tapePlayProcess.HasExited Then
                        tapePlayProcess.Kill()
                    End If
                Catch ex As InvalidOperationException
                Catch ex As NotSupportedException
                Catch ex As Exception
                    Debug.WriteLine($"Error stopping tapePlayProcess: {ex.Message}")
                End Try
            Finally
                tapePlayProcess?.Dispose()
                tapePlayProcess = Nothing
                If _childProcesses.Contains(tapePlayProcess) Then
                    _childProcesses.Remove(tapePlayProcess)
                End If
            End Try
        End If

        RunDeleteTempFiles()
    End Sub

    Private Sub RunDeleteTempFiles()
        Dim tempFilesToDelete As String() = {
        Path.Combine(Path.GetTempPath(), "cbm_current_block.txt"),
        Path.Combine(Path.GetTempPath(), "cbm_total_blocks.txt"),
        Path.Combine(Path.GetTempPath(), "cbm_next_block.txt"),
        Path.Combine(Path.GetTempPath(), "cbm_control.txt"),
        Path.Combine(Path.GetTempPath(), "cbm_position.txt"),
        Path.Combine(Path.GetTempPath(), "cbm_waiting.txt")
    }

        For Each filePath In tempFilesToDelete
            Try
                If File.Exists(filePath) Then
                    File.Delete(filePath)
                End If
            Catch ex As Exception
                Debug.WriteLine($"Failed to delete temp file {filePath}: {ex.Message}")
            End Try
        Next
    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        SaveShownText()
        _formClosing = True
        SaveLastGame()
        TerminateAllChildProcesses()

        If _gameFolderWatcher IsNot Nothing Then
            _gameFolderWatcher.Dispose()
        End If
    End Sub

    Private Sub SaveLastGame()
        If Not File.Exists(_settingsFilePath) Then Return

        Dim lines = File.ReadAllLines(_settingsFilePath).ToList()
        If lines.Count < 4 OrElse Not Boolean.Parse(lines(3)) Then Return

        Dim currentGame = If(ListBox1.SelectedItem?.ToString(), "")

        If lines.Count >= 5 Then
            lines(4) = currentGame
        Else
            lines.Add(currentGame)
        End If

        File.WriteAllLines(_settingsFilePath, lines)
    End Sub

    Private Sub ListBox1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ListBox1.SelectedIndexChanged
        If ListBox1.SelectedIndex = -1 Then Exit Sub

        ' Stop the timer if it's running
        If tapeCounterTimer.Enabled Then
            tapeCounterTimer.Stop()
        End If

        Dim selectedGame As String = ListBox1.SelectedItem.ToString()
        StartScrollingText(DisplayName(selectedGame))

        If selectedGame.Length > DISPLAY_CHARS Then
            scrollTimer.Start()
        End If

        If Not IsTapePlaying() Then
            ResetBlockLabels()
            RunDeleteTempFiles()
        End If

        ' Always reload assets when selection changes
        LoadGameAssets(selectedGame)
        _lastSelectedGame = selectedGame
        QueueFastLoadCheck()

        ' If a game is playing, restart the timer
        If tapePlayProcess IsNot Nothing AndAlso Not tapePlayProcess.HasExited Then
            tapeCounterTimer.Start()
        End If
    End Sub

    Private Sub LoadGameAssets(gameName As String)
        SaveShownText()
        ' A tagged entry can have its own files, or share the plain-named set
        Dim gameFilePath As String = FindGameFile(gameName)
        Dim plainName As String = If(gameFilePath Is Nothing, gameName, Path.GetFileNameWithoutExtension(gameFilePath))
        Dim assetNames As String() = {gameName, plainName}
        Dim gameSystemName As String = If(gameFilePath Is Nothing, "C64", GameSystem(gameFilePath))

        ' Chosen image type first, then the other types, then the old flat folder
        Dim imageSlots As New List(Of AssetSlot) From {ImageSlot(gameSystemName, _imageTypeOption)}
        imageSlots.AddRange(_imageTypeFolders.Where(Function(t) t <> _imageTypeOption).
                            Select(Function(t) ImageSlot(gameSystemName, t)))
        imageSlots.AddRange(LegacyAssetFolders().Take(1))
        Dim imagePath As String = FindInSlots(imageSlots, assetNames)

        Try
            ReleaseImageResources()
            If imagePath IsNot Nothing Then
                ' Create a copy of the image to avoid file locking
                Dim imageBytes As Byte() = File.ReadAllBytes(imagePath)
                Using ms As New MemoryStream(imageBytes)
                    PictureBox1.Image = Image.FromStream(ms)
                End Using
                _currentImagePath = imagePath
                PictureBox1.SizeMode = PictureBoxSizeMode.Zoom
            Else
                PictureBox1.Image = Nothing
                PictureBox1.SizeMode = PictureBoxSizeMode.Normal
            End If
        Catch ex As Exception
            PictureBox1.Image = Nothing
            PictureBox1.SizeMode = PictureBoxSizeMode.Normal
        End Try

        ' Game information, then the manual below it
        Dim infoPath As String = FindInSlots({InfoSlot(gameSystemName)}, assetNames)
        Dim manualSlots = {ManualSlot(gameSystemName), LegacyAssetFolders()(1)}
        Dim textSlots = manualSlots.Select(Function(slot) New AssetSlot(slot.Folder, slot.Suffix, {".txt"})).ToList()
        Dim manualPath As String = FindInSlots(textSlots, assetNames)
        ShowManualsFor(manualSlots, assetNames)
        Try
            Dim sections As New List(Of String)
            If infoPath IsNot Nothing Then sections.Add(FormatInfoForDisplay(File.ReadAllText(infoPath), TitleMarks(plainName)))
            If manualPath IsNot Nothing Then
                Dim manualText As String = File.ReadAllText(manualPath).TrimEnd()
                sections.Add(If(infoPath IsNot Nothing, "---------- Manual ----------" & Environment.NewLine & manualText, manualText))
            End If
            ' The first line is left empty: the Manual button sits over it
            RichTextBox1.Text = Environment.NewLine & If(sections.Count > 0,
                                   String.Join(Environment.NewLine & Environment.NewLine, sections),
                                   NO_TEXT_YET)
            _currentManualPath = If(manualPath, "")
            _shownTextGame = gameName
            _shownHadInfo = infoPath IsNot Nothing
            _shownHadManual = manualPath IsNot Nothing
            RichTextBox1.Modified = False
        Catch ex As Exception
            RichTextBox1.Text = "Unable to load game information"
            _currentManualPath = ""
        End Try
    End Sub

    Private Sub SearchBox_TextChanged(sender As Object, e As EventArgs) Handles SearchBox.TextChanged
        Dim newSearchTerm = SearchBox.Text.Trim().ToLower()
        If newSearchTerm <> _currentSearchTerm Then
            _currentSearchTerm = newSearchTerm
            ApplyGameFilter()
        End If
    End Sub

    Private Sub BtnPlay_Click(sender As Object, e As EventArgs) Handles BtnPlay.Click
        If tapePlayProcess Is Nothing OrElse tapePlayProcess.HasExited Then
            If ListBox1.SelectedIndex = -1 Then
                MessageBox.Show("Please select a game first!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim selectedGame As String = ListBox1.SelectedItem.ToString()
            Dim gameFile As String = FindGameFile(selectedGame)

            If gameFile Is Nothing Then
                MessageBox.Show("Game file not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
                Return
            End If

            Try
                If File.Exists(controlFilePath) Then File.Delete(controlFilePath)

                Dim backendArguments As String =
                    $"""{Path.Combine(Application.StartupPath, "cbmplay.py")}"" ""{gameFile}"" --control ""{controlFilePath}"" --machine {MachineFor(gameFile)}"
                If _invertOption Then backendArguments &= " --invert"
                If _sineOption Then backendArguments &= " --wave sine"
                If _leaderOption < 100 Then backendArguments &= $" --leader {_leaderOption}"
                ' Fast load types 2 to 5 are backend levels 1 to 4
                Dim fastType As Integer = EffectiveFastLoadType(selectedGame, KnownFastStatus(selectedGame))
                Dim turboLevel As Integer = If(fastType >= 2, fastType - 1, 0)
                If turboLevel > 0 Then backendArguments &= $" --turbo {turboLevel}"
                If Not _stopsOn.Contains(selectedGame) Then backendArguments &= " --no-stops"
                ' Built fast-load tapes are kept here and used again; cbmplay removes a
                ' game's kept tape when its speed changes or it plays at normal speed
                backendArguments &= $" --cache ""{Path.Combine(SystemRoot(GameSystem(gameFile)), "Cache")}"""

                Dim startInfo As New ProcessStartInfo() With {
                .FileName = Path.Combine(Application.StartupPath, "python", "python.exe"),
                .Arguments = backendArguments,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }

                tapePlayProcess = Process.Start(startInfo)
                _childProcesses.Add(tapePlayProcess)

                AddHandler tapePlayProcess.ErrorDataReceived, Sub(s, args)
                                                                 ShowBackendMessage(args.Data)
                                                             End Sub

                AddHandler tapePlayProcess.OutputDataReceived, Sub(s, args)
                                                                  ShowBackendMessage(args.Data)
                                                              End Sub

                tapePlayProcess.BeginErrorReadLine()
                tapePlayProcess.BeginOutputReadLine()

                ListBox1.Enabled = False
                tapeCounter = 0
                totalBlocks = GetTotalBlocks()
                isStopped = False
                BtnStop.Text = "Stop"
                zeroedBlock = -1
                LblZeroedBlock.Text = "Reset at: ---"
                SetTapeProgress(0)

                ' Ensure the timer is properly started
                tapeCounterTimer.Stop() ' Stop if already running
                tapeCounterTimer.Start() ' Start fresh

                ' Force an immediate UI update
                UpdateTapeCounter(Me, EventArgs.Empty)

            Catch ex As Exception
                MessageBox.Show($"Failed to start Python script: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
                tapeCounterTimer.Stop() ' Ensure timer is stopped if there's an error
            End Try
        Else
            ' Carries on after Stop, or after the tape stopped itself for the next part
            If isStopped OrElse File.Exists(_waitingFile) Then
                WriteControl("resume")
                isStopped = False
                BtnStop.Text = "Stop"
                tapeCounterTimer.Start() ' Restart the timer when resuming
                ShowWaiting(False)

                ' Force an immediate UI update
                UpdateTapeCounter(Me, EventArgs.Empty)
            End If
        End If
    End Sub

    Private Sub BtnStop_Click(sender As Object, e As EventArgs) Handles BtnStop.Click
        If tapePlayProcess Is Nothing OrElse tapePlayProcess.HasExited Then
            MessageBox.Show("No game is currently playing.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        WriteControl("pause")
        isStopped = True
    End Sub

    Private Async Sub BtnRewind_Click(sender As Object, e As EventArgs) Handles BtnRewind.Click
        If tapePlayProcess Is Nothing OrElse tapePlayProcess.HasExited Then
            MessageBox.Show("No game is currently playing.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        WriteControl("pause")
        isStopped = True

        Await Task.Delay(100)

        Dim currentBlock As Integer = GetCurrentBlock()
        currentBlock = Math.Max(0, currentBlock - 1)
        WriteControl($"rewind:{currentBlock}")
    End Sub

    Private Async Sub BtnFastForward_Click(sender As Object, e As EventArgs) Handles BtnFastForward.Click
        If tapePlayProcess Is Nothing OrElse tapePlayProcess.HasExited Then
            MessageBox.Show("No game is currently playing.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        WriteControl("pause")
        isStopped = True

        Await Task.Delay(100)

        If File.Exists(nextBlockFilePath) Then
            Try
                Dim targetBlock As Integer = Integer.Parse(File.ReadAllText(nextBlockFilePath))
                WriteControl($"jump:{targetBlock}")
            Catch ex As Exception
                Debug.WriteLine($"Fast-forward error: {ex.Message}")
            End Try
        End If
    End Sub

    Private Sub BtnEject_Click(sender As Object, e As EventArgs) Handles BtnEject.Click
        If tapePlayProcess IsNot Nothing AndAlso Not tapePlayProcess.HasExited Then
            WriteControl("stop")
            Try
                If Not tapePlayProcess.HasExited Then
                    tapePlayProcess.Kill()
                End If
            Catch ex As Exception
                Debug.WriteLine($"Error stopping process: {ex.Message}")
            Finally
                tapePlayProcess?.Dispose()
                tapePlayProcess = Nothing
                If _childProcesses.Contains(tapePlayProcess) Then
                    _childProcesses.Remove(tapePlayProcess)
                End If
            End Try
        End If

        isStopped = False
        ListBox1.Enabled = True
        tapeCounterTimer.Stop()
        ResetBlockLabels()
        RunDeleteTempFiles()
        LblTapeStatus.Text = "Ready"
    End Sub

    Private Sub BtnCounterReset_Click(sender As Object, e As EventArgs) Handles BtnCounterReset.Click
        zeroedBlock = GetCurrentBlock()
        Dim counter = ReadTapeCounter()
        LblZeroedBlock.Text = $"Reset at: {If(counter IsNot Nothing, counter(1), 0):D3}"
    End Sub

    Private Sub Jump_Click(sender As Object, e As EventArgs) Handles Jump.Click
        If tapePlayProcess Is Nothing OrElse tapePlayProcess.HasExited Then
            MessageBox.Show("No game is currently playing.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        If zeroedBlock >= 0 Then
            WriteControl($"jump:{zeroedBlock}")
        Else
            MessageBox.Show("No reset block position set. Use Counter Reset first.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End If
    End Sub

    Private Function GetCurrentBlock() As Integer
        Dim currentBlockFile As String = Path.Combine(Path.GetTempPath(), "cbm_current_block.txt")
        If Not File.Exists(currentBlockFile) Then Return 0

        Try
            Return Integer.Parse(File.ReadAllText(currentBlockFile))
        Catch
            Return 0
        End Try
    End Function

    Private Function GetTotalBlocks() As Integer
        Dim totalBlocksFile As String = Path.Combine(Path.GetTempPath(), "cbm_total_blocks.txt")
        If Not File.Exists(totalBlocksFile) Then Return 0

        Try
            Return Integer.Parse(File.ReadAllText(totalBlocksFile))
        Catch
            Return 0
        End Try
    End Function

    ' Drag-Drop functionality for PictureBox (images)
    Private Sub PictureBox1_DragEnter(sender As Object, e As DragEventArgs) Handles PictureBox1.DragEnter
        If Not _editModeEnabled Then
            e.Effect = DragDropEffects.None
            Return
        End If

        If e.Data.GetDataPresent(DataFormats.FileDrop) Then
            Dim files() As String = CType(e.Data.GetData(DataFormats.FileDrop), String())
            If files.Length = 1 AndAlso _allowedImageExtensions.Contains(Path.GetExtension(files(0)).ToLower()) Then
                e.Effect = DragDropEffects.Copy
            Else
                e.Effect = DragDropEffects.None
            End If
        Else
            e.Effect = DragDropEffects.None
        End If
    End Sub

    Private Sub PictureBox1_DragDrop(sender As Object, e As DragEventArgs) Handles PictureBox1.DragDrop
        If Not _editModeEnabled OrElse ListBox1.SelectedIndex = -1 Then
            MessageBox.Show("Please enable Edit Mode and select a game first!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Dim files() As String = CType(e.Data.GetData(DataFormats.FileDrop), String())
        If files.Length = 0 Then Return

        Try
            Dim selectedGame As String = ListBox1.SelectedItem.ToString()
            Dim gameFile As String = FindGameFile(selectedGame)
            Dim plainName As String = If(gameFile Is Nothing, selectedGame, Path.GetFileNameWithoutExtension(gameFile))
            Dim gameSystemName As String = If(gameFile Is Nothing, "C64", GameSystem(gameFile))
            Dim sourceFile = files(0)

            ' Saved as the image type on show, so downloads never replace it
            Dim slot As AssetSlot = ImageSlot(gameSystemName, _imageTypeOption)
            Directory.CreateDirectory(slot.Folder)
            Dim destPath As String = slot.PathFor(plainName, Path.GetExtension(sourceFile).ToLower())

            For Each ext In slot.Extensions
                If File.Exists(slot.PathFor(plainName, ext)) Then SafeDeleteFile(slot.PathFor(plainName, ext))
            Next

            File.Copy(sourceFile, destPath, True)
            LoadGameAssets(selectedGame)
            MessageBox.Show($"Image saved as {Path.GetFileName(destPath)}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show($"Error saving image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ' Drag-Drop functionality for RichTextBox (manuals)
    Private Sub RichTextBox1_DragEnter(sender As Object, e As DragEventArgs) Handles RichTextBox1.DragEnter
        If Not _editModeEnabled Then
            e.Effect = DragDropEffects.None
            Return
        End If

        If e.Data.GetDataPresent(DataFormats.FileDrop) Then
            Dim files() As String = CType(e.Data.GetData(DataFormats.FileDrop), String())
            If files.Length = 1 AndAlso _allowedManualExtensions.Contains(Path.GetExtension(files(0)).ToLower()) Then
                e.Effect = DragDropEffects.Copy
            Else
                e.Effect = DragDropEffects.None
            End If
        Else
            e.Effect = DragDropEffects.None
        End If
    End Sub

    Private Sub RichTextBox1_DragDrop(sender As Object, e As DragEventArgs) Handles RichTextBox1.DragDrop
        If Not _editModeEnabled OrElse ListBox1.SelectedIndex = -1 Then
            MessageBox.Show("Please enable Edit Mode and select a game first!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Dim files() As String = CType(e.Data.GetData(DataFormats.FileDrop), String())
        If files.Length = 0 Then Return

        Try
            Dim selectedGame As String = ListBox1.SelectedItem.ToString()
            Dim gameFile As String = FindGameFile(selectedGame)
            Dim plainName As String = If(gameFile Is Nothing, selectedGame, Path.GetFileNameWithoutExtension(gameFile))
            Dim gameSystemName As String = If(gameFile Is Nothing, "C64", GameSystem(gameFile))
            Dim slot As AssetSlot = ManualSlot(gameSystemName)
            Directory.CreateDirectory(slot.Folder)
            Dim destPath As String = slot.PathFor(plainName, ".txt")

            SafeDeleteFile(destPath)

            File.Copy(files(0), destPath, True)
            LoadGameAssets(selectedGame)
            MessageBox.Show($"Manual saved as {Path.GetFileName(destPath)}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show($"Error saving manual: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub
End Class