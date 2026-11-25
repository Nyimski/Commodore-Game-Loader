Imports System.Drawing
Imports System.Windows.Forms

' The Help window: a list of topics on the left, the chosen page on the right and a
' search box above. Every page is written below, so no Instructions.txt is needed.
'
' Page markup, one rule per line:
'   # Heading        ## Smaller heading
'   - Bullet point   > Tip, shown in a shaded box
'   **word** makes a word bold anywhere in a line.
Public Class HelpForm
    Inherits Form

    Private Class HelpTopic
        Public Title As String
        Public Body As String
        Public Plain As String

        Public Sub New(topicTitle As String, pageText As String)
            Title = topicTitle
            Body = pageText.Replace(vbCr, "").Trim(ControlChars.Lf)
            Plain = (topicTitle & " " & Body).Replace("**", "")
        End Sub

        Public Overrides Function ToString() As String
            Return Title
        End Function
    End Class

    Private ReadOnly _topics As New List(Of HelpTopic)
    Private ReadOnly _topicList As New ListBox()
    Private ReadOnly _page As New RichTextBox()
    Private ReadOnly _searchBox As New TextBox()
    Private ReadOnly _matchLabel As New Label()

    Private ReadOnly _bodyFont As New Font("Segoe UI", 10)
    Private ReadOnly _boldFont As New Font("Segoe UI", 10, FontStyle.Bold)
    Private ReadOnly _headingFont As New Font("Segoe UI", 16, FontStyle.Bold)
    Private ReadOnly _subheadingFont As New Font("Segoe UI", 11.5F, FontStyle.Bold)
    Private ReadOnly _headingColour As Color = Color.FromArgb(30, 60, 120)
    Private ReadOnly _tipBack As Color = Color.FromArgb(255, 248, 220)

    Public Sub New(Optional startTopic As String = Nothing)
        Me.Text = "Commodore Game Loader Help"
        Try
            Me.Icon = Form1.Icon
        Catch ex As Exception
            Debug.WriteLine($"No icon for Help: {ex.Message}")
        End Try
        Me.Size = New Size(980, 700)
        Me.MinimumSize = New Size(640, 420)
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Font = New Font("Segoe UI", 9.5F)
        Me.BackColor = Color.White
        Me.KeyPreview = True

        AddTopics()

        ' Search across every page
        Dim searchPanel As New Panel() With {.Dock = DockStyle.Top, .Height = 40, .Padding = New Padding(10, 8, 10, 4)}
        _searchBox.PlaceholderText = "Search help (for example: fast load, RAM, manual, red screen)"
        _searchBox.Dock = DockStyle.Fill
        _matchLabel.Dock = DockStyle.Right
        _matchLabel.Width = 170
        _matchLabel.TextAlign = ContentAlignment.MiddleRight
        _matchLabel.ForeColor = Color.DimGray
        searchPanel.Controls.Add(_searchBox)
        searchPanel.Controls.Add(_matchLabel)
        AddHandler _searchBox.TextChanged, Sub(s, args) FilterTopics()

        ' Topics on the left, the page on the right
        Dim split As New SplitContainer() With {.Dock = DockStyle.Fill, .FixedPanel = FixedPanel.Panel1, .SplitterWidth = 6}
        _topicList.Dock = DockStyle.Fill
        _topicList.BorderStyle = BorderStyle.None
        _topicList.Font = New Font("Segoe UI", 10.5F)
        _topicList.IntegralHeight = False
        _topicList.BackColor = Color.FromArgb(244, 246, 250)
        _topicList.ItemHeight = 26
        _topicList.DrawMode = DrawMode.OwnerDrawFixed
        AddHandler _topicList.DrawItem, AddressOf DrawTopic
        AddHandler _topicList.SelectedIndexChanged, Sub(s, args) ShowSelectedTopic()
        split.Panel1.Controls.Add(_topicList)
        split.Panel1.Padding = New Padding(10, 4, 0, 0)

        _page.Dock = DockStyle.Fill
        _page.ReadOnly = True
        _page.BackColor = Color.White
        _page.BorderStyle = BorderStyle.None
        _page.ScrollBars = RichTextBoxScrollBars.Vertical
        _page.DetectUrls = False
        _page.ShortcutsEnabled = True
        split.Panel2.Controls.Add(_page)
        split.Panel2.Padding = New Padding(16, 4, 10, 0)

        ' Close button
        Dim bottomPanel As New Panel() With {.Dock = DockStyle.Bottom, .Height = 48, .Padding = New Padding(10, 8, 10, 8)}
        Dim closeButton As New Button() With {.Text = "Close", .DialogResult = DialogResult.OK, .Dock = DockStyle.Right, .Width = 100}
        bottomPanel.Controls.Add(closeButton)

        Me.Controls.Add(split)
        Me.Controls.Add(searchPanel)
        Me.Controls.Add(bottomPanel)
        Me.AcceptButton = closeButton
        Me.CancelButton = closeButton

        AddHandler Me.Load, Sub(s, args)
                                split.SplitterDistance = 250
                                FilterTopics()
                                Dim first As HelpTopic = _topics.FirstOrDefault(Function(t) startTopic IsNot Nothing AndAlso
                                                                                     t.Title.Equals(startTopic, StringComparison.OrdinalIgnoreCase))
                                If first IsNot Nothing Then _topicList.SelectedItem = first
                                _topicList.Focus()
                            End Sub
        AddHandler Me.KeyDown, Sub(s, args)
                                   If args.Control AndAlso args.KeyCode = Keys.F Then
                                       _searchBox.Focus()
                                       _searchBox.SelectAll()
                                       args.SuppressKeyPress = True
                                   End If
                               End Sub
    End Sub

    ' --- Topic list and search ---------------------------------------------

    Private Sub FilterTopics()
        Dim term As String = _searchBox.Text.Trim()
        Dim keep As Object = _topicList.SelectedItem
        _topicList.BeginUpdate()
        _topicList.Items.Clear()
        For Each topic In _topics
            If term = "" OrElse topic.Plain.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 Then _topicList.Items.Add(topic)
        Next
        _topicList.EndUpdate()

        If term = "" Then
            _matchLabel.Text = ""
        Else
            _matchLabel.Text = If(_topicList.Items.Count = 1, "1 page found", $"{_topicList.Items.Count} pages found")
        End If

        If _topicList.Items.Count = 0 Then
            _page.Clear()
            AddRun("Nothing in Help matches """ & term & """. Try a shorter word.", _bodyFont, Color.DimGray)
        ElseIf keep IsNot Nothing AndAlso _topicList.Items.Contains(keep) Then
            _topicList.SelectedItem = keep
            ShowSelectedTopic()
        Else
            _topicList.SelectedIndex = 0
        End If
    End Sub

    Private Sub DrawTopic(sender As Object, e As DrawItemEventArgs)
        If e.Index < 0 Then Return
        Dim picked As Boolean = (e.State And DrawItemState.Selected) = DrawItemState.Selected
        Using back As New SolidBrush(If(picked, _headingColour, _topicList.BackColor))
            e.Graphics.FillRectangle(back, e.Bounds)
        End Using
        Dim box As New Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height)
        TextRenderer.DrawText(e.Graphics, _topicList.Items(e.Index).ToString(), _topicList.Font, box,
                              If(picked, Color.White, Color.FromArgb(30, 30, 30)),
                              TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix)
    End Sub

    Private Sub ShowSelectedTopic()
        Dim topic = TryCast(_topicList.SelectedItem, HelpTopic)
        If topic Is Nothing Then Return
        RenderPage(topic)
        HighlightSearch()
    End Sub

    Private Sub HighlightSearch()
        Dim term As String = _searchBox.Text.Trim()
        Dim firstHit As Integer = -1
        If term <> "" Then
            Dim at As Integer = _page.Find(term, 0, RichTextBoxFinds.None)
            While at >= 0
                If firstHit < 0 Then firstHit = at
                _page.Select(at, term.Length)
                _page.SelectionBackColor = Color.FromArgb(255, 230, 120)
                If at + term.Length >= _page.TextLength Then Exit While
                at = _page.Find(term, at + term.Length, RichTextBoxFinds.None)
            End While
        End If
        _page.SelectionStart = If(firstHit >= 0, firstHit, 0)
        _page.SelectionLength = 0
        _page.ScrollToCaret()
    End Sub

    ' --- Drawing a page ------------------------------------------------------

    Private Sub RenderPage(topic As HelpTopic)
        _page.SuspendLayout()
        _page.Clear()
        For Each rawLine In topic.Body.Split(ControlChars.Lf)
            Dim lineText As String = rawLine.TrimEnd()
            If lineText.StartsWith("## ") Then
                AddRun(Environment.NewLine, _bodyFont, Color.Black)
                AddRun(lineText.Substring(3) & Environment.NewLine, _subheadingFont, _headingColour)
            ElseIf lineText.StartsWith("# ") Then
                AddRun(lineText.Substring(2) & Environment.NewLine, _headingFont, _headingColour)
            ElseIf lineText.StartsWith("- ") Then
                StartParagraph(bullet:=True)
                AddInline(lineText.Substring(2), Color.Black)
                AddRun(Environment.NewLine, _bodyFont, Color.Black)
                EndParagraph()
            ElseIf lineText.StartsWith("> ") Then
                StartParagraph(bullet:=False, indent:=12)
                Dim before As Integer = _page.TextLength
                AddRun("Tip:  ", _boldFont, Color.FromArgb(150, 90, 0))
                AddInline(lineText.Substring(2), Color.FromArgb(60, 45, 0))
                _page.Select(before, _page.TextLength - before)
                _page.SelectionBackColor = _tipBack
                _page.Select(_page.TextLength, 0)
                AddRun(Environment.NewLine, _bodyFont, Color.Black)
                EndParagraph()
            Else
                AddInline(lineText, Color.Black)
                AddRun(Environment.NewLine, _bodyFont, Color.Black)
            End If
        Next
        _page.ResumeLayout()
    End Sub

    Private Sub StartParagraph(bullet As Boolean, Optional indent As Integer = 0)
        _page.Select(_page.TextLength, 0)
        _page.SelectionBullet = bullet
        _page.BulletIndent = 14
        _page.SelectionIndent = If(bullet, 10, indent)
    End Sub

    Private Sub EndParagraph()
        _page.Select(_page.TextLength, 0)
        _page.SelectionBullet = False
        _page.SelectionIndent = 0
    End Sub

    ' Plain text with **bold** words
    Private Sub AddInline(lineText As String, colour As Color)
        Dim parts As String() = lineText.Split({"**"}, StringSplitOptions.None)
        For index As Integer = 0 To parts.Length - 1
            If parts(index) = "" Then Continue For
            AddRun(parts(index), If(index Mod 2 = 1, _boldFont, _bodyFont), colour)
        Next
    End Sub

    Private Sub AddRun(runText As String, runFont As Font, colour As Color)
        _page.Select(_page.TextLength, 0)
        _page.SelectionFont = runFont
        _page.SelectionColor = colour
        _page.SelectionBackColor = Color.White
        _page.SelectedText = runText
    End Sub

    ' --- The pages -------------------------------------------------------------

    Private Sub AddTopics()
        _topics.Add(New HelpTopic("Quick Start", "
# Quick Start
Commodore Game Loader plays your Commodore 64, VIC-20, C16 and Plus/4 games from the PC's sound output into a real machine, just like a cassette player would.

## First time
- Connect the PC's headphone or line output to your Commodore's cassette port (see **Connecting Your Commodore**).
- Choose your machine from the **System** menu: Commodore 64, VIC-20 or C16 / Plus/4.
- Put your games in that system's **Games** folder, or use **Edit Mode → Import Games From Folder…** to copy them in.
- Optionally switch Edit Mode on and use **Download Images** and **Download Game Info** to add pictures, years, publishers, genres and descriptions.

## Loading a game
- Click a game in the list.
- On a C64 or VIC-20, type **LOAD** and press **RETURN**. On a C16, C116 or Plus/4, type **LOAD"""",1** and press **RETURN**. The screen says PRESS PLAY ON TAPE.
- Click **Play** in the app.
- Wait. Most games start by themselves when loading finishes. If one stops at READY, type **RUN** and press **RETURN**.

> Turn the PC's volume up high and switch off any sound enhancements or equaliser. Most loading problems are a volume or polarity issue.
"))

        _topics.Add(New HelpTopic("Connecting Your Commodore", "
# Connecting Your Commodore
The app makes the same sound a cassette makes, so you need a cable or adaptor that feeds audio into the Commodore's cassette port.

## Commodore 64 and VIC-20
- These use the wide edge-connector cassette port at the back, the one a C2N / 1530 Datasette plugs into.
- Use an audio-to-cassette adaptor made for this port, or a Datasette modified to take an audio input (see below).

## C16, C116 and Plus/4
- These use a small round 7-pin cassette socket, the one the 1531 Datasette plugs into.
- You need an adaptor made for this socket, or a C64-style adaptor plus a converter: one end plugs into the C16 and the other end is a standard C64 / VIC-20 cassette port.

## Devices that work with the app
These take the sound from the PC's headphone or line output and feed it to the cassette port:
- **µCassette (uCassette)**: plugs into the cassette port, with a 3.5 mm audio cable to the PC. For the C64, C128 and VIC-20, and the C16 and Plus/4 with the converter above.
- **MP32C64 / DatassetteAudioAdapter**: an open-hardware adaptor for the VIC-20, C64, C128 and Plus/4.
- **Cassadapt**: connects the PC's sound card to the C64 or C16.
- **A Datasette with an audio input mod**, such as the C2N Aquarius mod.
- **An ordinary Datasette with a car cassette adaptor in it**: this can work, but it's less reliable, because those adaptors are made for music rather than data.

## Getting the level right
- Set the PC's volume high, usually 80 to 100 percent.
- Turn off sound enhancements, spatial sound and equalisers in Windows for that output.
- Use the headphone or line output, not Bluetooth.
- If games never load, tick **Invert signal** in **Settings → Tape Options**. Some adaptors flip the signal.
- If loading is unreliable with a square wave, try **Sine wave output**.

> Tape Options are kept separately for each system. An adaptor may need Invert signal on one machine and not on another, so set them while that system is chosen.
"))

        _topics.Add(New HelpTopic("The Game List", "
# The Game List
The list shows every game in the current system's Games folder, including games in subfolders.

## Columns
- **Title**: the game's name. File names are tidied for display: underscores become spaces and words are capitalised.
- Which side, tape, part or disk a file is, and whether it has a cheat or trainer, always shows after the name in brackets, however the file name writes it. ""Elite - Tape 1 Side A"" shows as **Elite (Tape 1) (Side A)**, so the parts sort together, in order, and aren't taken for duplicates. The file names themselves aren't changed.
- Downloads search for the game's name without those marks, so every part of a game finds its details and pictures. The info file's Title keeps the marks.
- **RAM** (VIC-20 and C16 / Plus/4): how much memory the game needs. See the pages for those machines.
- **Machine** (C16 / Plus/4 only): whether the game needs a C16 or a Plus/4.
- **Year**, **Publisher** and **Genre**: from the game's info, once downloaded or typed in.

## Finding games
- Type in the **search box** to show only games whose title, year, publisher, genre or RAM matches. Several words must all match.
- The **view dropdown** beside it shows All Games, Favourites, or one genre from the list under **Genres**. The same choice is in **View → Show Games**.
- The **RAM** dropdown (VIC-20 and C16) and **Machine** dropdown (C16) show only games that fit your machine.
- The window title shows how many games are listed out of the total.

## Favourites
- Right-click a game and choose **Add to Favourites**. Favourites have a ★ in front.
- This works whether Edit Mode is on or off.

## Pictures
- The picture beside the list shows the game's case, title screen or in-game screen.
- Choose which one in **View → Show Image**.
"))

        _topics.Add(New HelpTopic("Playing a Tape", "
# Playing a Tape
The Tape Status panel works like a cassette deck.

## Buttons
- **Play**: starts the selected game. If the tape is stopped, Play carries on from where it stopped.
- **Stop**: pauses the tape. The Commodore waits, just as it does when a real tape stops.
- **Rewind**: goes back one block. Use this after a loading error, then press Play.
- **Fast Forward**: skips to the start of the next block.
- **Counter Reset**: marks the current position, like zeroing a tape counter. The position is shown as Reset at.
- **Jump**: goes back to the position you marked with Counter Reset. Useful for multi-load games that ask for the same part again.
- **Eject**: stops the tape completely so you can choose another game.

## While loading
- The **Tape Counter**, block number and percentage show how far the tape has got.
- Before you press Play, the Tape Status box shows the speed the game will use and how many blocks it has. How long it will take shows at the bottom left, after the game's name: **Load Time**.
- If a message is too long for the Tape Status box, it ends with … and the whole of it shows when you hold the mouse over it.
- Scanning, downloading and other jobs running in the background show their progress at the right-hand end of the menu bar.
- The game list is locked while a tape is playing. Press **Eject** to choose another game.

## Games in several parts
- Some tapes hold more than one program, for example a loader and then the main game, or a loading screen and then each level.
- Normally the tape plays straight through. For a game that needs the tape stopped between parts, such as one that asks you to press PLAY again for the next level, switch Edit Mode on, right-click the game and tick **Stop Tape Between Parts**.
- With that ticked, the tape stops itself where the original tape was stopped, as a Datasette's motor would. The status box says **Press Play for the next part**.
- When the game asks for the next part (PRESS PLAY ON TAPE, or a message of its own), click **Play**. You can take as long as you like on a title screen first.
- The app finds these places in two ways: before each later standard part, and at each long silence on the tape (two seconds or more), where the motor was off when it was recorded.
- This works for every system, with fast load on or off. VIC-20 games fast loaded in several parts always stop before each part (see Fast Load), as their loader needs it.
"))

        _topics.Add(New HelpTopic("Fast Load", "
# Fast Load
Fast load sends a tiny loader at normal speed, then sends the game itself many times faster. A game that takes ten minutes can load in under a minute.

## Which games can fast load
- The game must start with a program that runs from BASIC. PRG files are the most likely to work, and so are TAP and audio files that hold standard programs.
- On the VIC-20, tapes in several parts can fast load too: see **Games in several parts (VIC-20)** below.
- Games with their own loader, and very large programs, load at normal speed instead. The app does this by itself.
- When you click a game, the panel shows **Fast Load: On**, **Off**, **Unavailable** or **Checking…**. Hover over Unavailable to see why.

## Games in several parts (VIC-20)
- Many VIC-20 tapes are a small first program, such as a title screen or instructions, that then loads the main game.
- With fast load, the first part loads and runs. The fast loader then stays in memory, so when the game asks for the next part, it fast loads too.
- The tape stops itself before each later part, just as a Datasette's motor would. The status box says **Press Play for the next part**.
- When the VIC-20 shows **PRESS PLAY ON TAPE**, click **Play**. You can take as long as you like on a title screen first.
- You don't need to press Stop after a part: the tape stops itself each time.
- Each part goes to the memory address its own tape header gives, just as LOAD"""",1,1 would. A part that loads wrongly turns the screen red: press **Rewind**, then **Play**.
- A few games read the tape their own way, or overwrite the loader's memory. If one stops after the first part, set its Fast Load to **Off**.

## Speeds
- In Edit Mode, the **Fast Load** dropdown in the Tape Status panel sets the speed for the selected game.
- **Ultra** is the fastest and the default. Then **Hyper**, **Turbo**, **Fast** and **Off** (normal speed).
- The choice is saved for that game only. If a game fails at one speed, try a slower one, or Off.

## What you'll see on screen
- **Commodore 64**: the border changes colour as each block arrives: black, white, purple and green.
- **VIC-20**: the border changes colour as each block arrives, through the same four colours. The screen stays on.
- **C16 / Plus/4**: the screen is switched off while loading so the machine can run at full speed. A full screen of one colour is normal, and the border colour changes as each block arrives, through the same four colours.
- **Red** is never used for a good block. It means a block was read wrongly, and the loader waits for it again: press **Rewind**, then **Play**.
- This is the app's own fast load. A game with its own loader, or one loading at normal speed, may flash any colours it likes, red included: that means nothing.

> If red appears at the same place every time, lower the speed for that game, check the volume, or try Invert signal.

> A game that loads at normal speed but not with fast load can simply be set to Off. Converting a TAP to PRG first (see Convert to PRG) often makes fast load possible.
"))

        _topics.Add(New HelpTopic("Commodore 64", "
# Commodore 64
## Loading
- Type **LOAD** and press **RETURN**, or hold **SHIFT** and press **RUN/STOP** to load and run the first program on the tape.
- The screen says PRESS PLAY ON TAPE. Click **Play** in the app.
- The screen goes blank while loading at normal speed. This is normal.
- When it finishes, the game usually starts by itself. If it stops at READY, type **RUN** and press **RETURN**.

## Fast load
- Supported for single programs that load at the normal start of BASIC.
- Programs that load over the machine's input/output area cannot be fast loaded and play at normal speed.

## Stopping a game
- **RUN/STOP + RESTORE** stops many BASIC programs. Most machine-code games need the power switching off and on.
"))

        _topics.Add(New HelpTopic("VIC-20", "
# VIC-20
## Loading
- Type **LOAD** and press **RETURN**, or hold **SHIFT** and press **RUN/STOP**.
- Click **Play** in the app when it says PRESS PLAY ON TAPE.
- Fast-loaded games start by themselves. Others may need **RUN** and **RETURN**.
- For tapes in several parts, the tape stops before each later part. Click **Play** when the VIC-20 says PRESS PLAY ON TAPE (see Fast Load).

## Memory (the RAM column)
The VIC-20 has very little memory, and many games need a memory expansion cartridge. The RAM column shows what each game needs.
- **None**: an unexpanded VIC-20.
- **3K**: needs a 3K expansion.
- **8K**, **16K**, **24K** or **32K**: needs at least that much expansion.
- **A000**: needs RAM in the cartridge area (block 5), as used by some converted cartridge games.
- **?**: the app checked the game but could not tell. Most of these games have their own loader.
- **…**: not checked yet. The scan fills these in. While it runs with a RAM size picked, games move to the right size by themselves.
- Use the **RAM** dropdown to show only games for your setup.
- If the RAM shown is wrong, switch Edit Mode on, right-click the game and choose **RAM**.

## Memory pitfalls
- A game for an unexpanded VIC-20 can fail with 8K or more fitted, because the expansion moves the screen and BASIC. Remove the cartridge, or switch its memory off if it has switches.
- A game that needs 8K or more will not run on a 3K expansion.
- If a game stops with ?LOAD ERROR or OUT OF MEMORY partway through, check the RAM column against the cartridge you have fitted.
"))

        _topics.Add(New HelpTopic("C16 and Plus/4", "
# C16, C116 and Plus/4
These machines share a tape format and most of their software. The C116 is a C16 in a different case, with the same 16K of memory.

## Loading
- Type **LOAD"""",1** and press **RETURN**, then click **Play** in the app. The ,1 tells the machine to load from tape (device 1).
- If a game stops at READY, type **RUN** and press **RETURN**. **F6** (SHIFT + F3) types RUN for you.

## RAM and Machine columns
- **RAM** shows 16K, 32K or 64K: the memory the game needs.
- **Connecting**: a C64 / VIC-20 audio adaptor such as the µCassette works through a converter that plugs into the C16's round tape socket (see Connecting Your Commodore).
- **Machine** shows **C16** for games that fit in 16K and **Plus/4** for games that need more. A Plus/4 runs nearly all C16 games.
- A **?** means the app is not sure yet. It learns more after the game has been checked or its info has been downloaded.
- Use the **RAM** and **Machine** dropdowns to show only the games your machine can run.
- If a value is wrong, switch Edit Mode on, right-click the game and choose **RAM**, or use **Edit Details…**.

## Game info
- For this system, details, pictures and memory sizes come first from Plus/4 World, then from other sources.

## Fast load
- The screen is switched off while loading, so a full screen of one colour is normal. The border colour changes as each block arrives, and turns red if a block needs reading again.
- A few games load completely but then do not start. Set Fast Load to **Off** for those games; they load normally at standard speed.
"))

        _topics.Add(New HelpTopic("Game Info and Pictures", "
# Game Info and Pictures
## Downloading
Switch Edit Mode on, then use the Edit Mode menu:
- **Download Images (All Games)**: choose Game Case, Title Screen, Game Screen or All Three.
- **Download Game Info (All Games)**: year, publisher, developer, genre, players, memory and a description, where available. C64 games also get **Music** and **Review** lines when The Commodore Zone has them.
- **Download Info and Images (All Games)**: both in one run, such as overnight.
- For one game or a few, select them, right-click and choose **Download Images** or **Download Info** (see Edit Mode).
Downloads run in the background, so you can keep using the app. Pictures a game already has are kept, and games that weren't found recently aren't looked up again straight away.

## Speed
- Several games are looked up at once.
- Wikipedia's list of each system's games is read once a week, and The Commodore Zone's whole C64 list once a month (a minute or two, shown at the right of the menu bar). Games are then matched on your PC, so a game a site doesn't have costs no time at all.
- TheGamesDB is still asked one game at a time, and only for what the other sources didn't have.

## Filling in missing details
- A game that already has an info file is looked up again if any of Year, Publisher, Developer, Genre or Players is empty, or it has no description.
- Only the empty parts are filled in. Anything already there, including your own edits, is never changed.
- The free sources are asked first. TheGamesDB is only used for what they couldn't supply, to save your monthly allowance.
- If nothing more can be found for a game, it isn't asked about again for 30 days. Click the download again once everything else is done to try those games anyway.

## Where it comes from
- **libretro thumbnails**: case, title and screen pictures.
- **Plus/4 World**: for C16 / Plus/4 games, including the RAM each needs.
- **The Commodore Zone**: for C64 games: year, publisher, programmer, musician, genre, players and magazine review (such as Zzap!64's rating), plus a game screen picture. It's asked before TheGamesDB, which is then only used for what it's missing.
- **TheGamesDB**: extra info and pictures, if you've added a free key in **Settings → Sources**.
- **Wikipedia**: descriptions for better-known games.

## Possible matches
- When a name is close but not exact, the app lists the games it isn't sure of in the **Possible Matches** window.
- Pick the right title for each game you want to match, then click **Use These**.

## The info box
- Details such as Title, Year and Publisher are at the top, one per line. The description is below, split into paragraphs.
- The first line is left empty so the **Manual** button doesn't cover anything.
- In Edit Mode you can type straight into the info box. Changes are saved when you click away.

> The free TheGamesDB key allows about 1,000 requests a month. When it runs out, the app carries on with the other sources until the allowance resets.
"))

        _topics.Add(New HelpTopic("Manuals", "
# Manuals
## Viewing
- The **Manual** button sits at the top right of the info box. It is greyed out when the game has no manual.
- Text manuals and pictures open in a window inside the app.
- PDF and web-page manuals open in an app window if Microsoft Edge WebView2 is available. Otherwise they open in Windows' default viewer.

## Adding manuals
- For one or more games, switch Edit Mode on, right-click and choose **Add Manual…**.
- For a whole collection, use **Edit Mode → Import Manuals From Folder…**. The app matches files to games by name, and asks about any it isn't sure of.
- You can also drop a manual file onto the info box in Edit Mode.

## File types
- PDF, text, web pages and pictures are all accepted.
- If a download comes with extra files such as _meta.json or _jp2 folders, only the PDF is needed.

> A manual is matched to a game by file name. Naming it the same as the game, or as the game plus a note in brackets, is the surest way.
"))

        _topics.Add(New HelpTopic("Edit Mode", "
# Edit Mode
Edit Mode protects your collection from accidental changes. Switch it on in **Edit Mode → Editor Off** (it changes to Editor On).

## What Edit Mode unlocks
- The download and import items in the Edit Mode menu.
- The **Fast Load** speed dropdown in the Tape Status panel.
- Typing in the info box.
- Dragging files in: game files onto the list, pictures onto the picture, and text or manuals onto the info box.
- Selecting several games with **Shift**-click or **Ctrl**-click.

## Right-click menu
- **Add to Favourites** / **Remove from Favourites**.
- **Edit Details…**: change the title, RAM, year, publisher, genre and other details in one window.
- **Download Images** / **Download Info**: fetch any missing pictures, or fill any missing details, for the selected games only. Games not found before are tried again straight away.
- **Genre**: pick a genre, No Genre, or **New Genre…** to create your own. Several genres can be separated with commas.
- **RAM**: set the memory a game needs (VIC-20 and C16 / Plus/4).
- **Stop Tape Between Parts**: off for every game until ticked here. Tick it for a game that needs the tape stopped between parts (see Playing a Tape).
- **Rename**: renames the game file, and its pictures, info and manual with it.
- **Add Manual…**: attach a manual file.
- **Move**: move games to another folder.
- **Delete**: delete games, with a confirmation first.
- **Convert to PRG**: see the Convert to PRG page.
When several games are selected, the menu acts on all of them and says how many.
"))

        _topics.Add(New HelpTopic("Adding Games", "
# Adding Games
## Import a folder
- **Edit Mode → Import Games From Folder…** copies every game it finds in a folder and all its subfolders into the current system's Games folder.
- Files already there are skipped. The copy runs in the background.

## Drag and drop
- In Edit Mode, drag game files from Windows onto the game list.

## Find Missing Games
- **Edit Mode → Find Missing Games** lists titles known for this system that aren't in your collection. It doesn't download anything.
- Use **Browse…** to point at a folder of your own files. Any that match are marked Found and can be imported with **Import** or **Import All**.
- **Save List…** saves the list as a text file.

## Accepted files
- Tape images: **.tap**, **.tzx**, **.csw**
- Programs: **.prg**, **.p00**, **.t64**
- Audio recordings: **.wav**, **.mp3**, **.flac**, **.ogg**
- Compressed: **.gz**
"))

        _topics.Add(New HelpTopic("Convert to PRG", "
# Convert to PRG
A PRG file is the program itself, without the tape recording around it. PRGs load more reliably, and they are the most likely to fast load.

## Converting
- Switch Edit Mode on, select one or more games, right-click and choose **Convert to PRG**.
- The app reads the program from the TAP or audio file and saves it as a PRG with the same name.
- The original files are moved to the **Originals** folder beside the Games folder, so nothing is lost.

## What can't be converted
- Games with their own custom loader, and tapes with several programs, can't be turned into one PRG. They are left as they are, and the app says why.
- Damaged recordings where no good copy of the program can be read.
"))

        _topics.Add(New HelpTopic("Settings", "
# Settings
Open **Settings** from the menu bar. Folder settings are kept separately for each system; the window title shows which one you're changing.

## Folders
- **Games**, **Images**, **Manuals** and **Info**: leave a box blank to use the standard folder inside the app folder, or click **Browse** to choose your own.
- **Remember last game**: selects the game you last used when the app opens.

## Tape Options
- **Invert signal**: try this if games never load. It depends on your adaptor.
- **Sine wave output**: a smoother signal, which some adaptors prefer.
- **Short leader (test)**: Normal, Half or Quarter. Shortens the tone before each standard file, so loading starts sooner. Normal-speed games gain the most: about 6 seconds a part on the VIC-20. Leader tone that covers a wait (FOUND on the C64 and C16) is left alone. Games with their own loader keep their own tone. If loading becomes unreliable, set it back to Normal.
- These are kept separately for each system: they change only the system chosen at the time. Short Leader is also in **Tape Options** on the menu bar.

## Sources
- **TheGamesDB API key**: paste your free key here for extra game info and pictures. Tick **Show key** to check it.
- The key is kept only on this PC, in thegamesdb_key.txt beside the app.

> Keep thegamesdb_key.txt private. Don't upload it to GitHub or share it with the app.
"))

        _topics.Add(New HelpTopic("Files and Folders", "
# Files and Folders
Each system has its own folder inside the app folder: **C64**, **VIC20** and **C16**.

## Inside each system folder
- **Games**: your game files. Subfolders are fine.
- **Images**: pictures, in **Case**, **Title** and **Screen** subfolders, named the same as the game.
- **Manuals**: manual files, named the same as the game.
- **Info**: one text file per game holding its details and description.
- **Settings**: that system's settings, favourites, fast load choices and title changes.
- **Originals**: files moved aside by Convert to PRG.
- **Cache**: fast-load tapes the app has already built, so Play starts at once next time. Each game keeps one, for the speed it last played at; changing its Fast Load speed, or setting it to Off, removes it. Ones not used for six months are removed by themselves. The folder can be deleted at any time.

## Your own folders
- If you choose your own Images folder in Settings, pictures sit side by side: the case uses the game's name, and the title and screen pictures add **- Title** or **- Screen** after it.

> Back up the Settings and Info folders now and then. They hold everything you've edited by hand.
"))

        _topics.Add(New HelpTopic("Troubleshooting", "
# Troubleshooting
## The Commodore never finds the game
- Turn the volume up and switch off sound enhancements.
- Tick **Invert signal** in Settings → Tape Options and try again.
- Check the cable is in the headphone or line output and fully in the cassette port.

## ?LOAD ERROR or loading stops partway
- On a VIC-20, check the RAM column against your memory cartridge.
- Try **Sine wave output**, or a slightly lower volume if it is at 100 percent.
- If the game uses fast load, set it slower or Off.

## Red screen or red border during fast load
- With the app's fast load, red only ever means a block was read wrongly. Press **Rewind**, then **Play**.
- Red stripes or flashes from a game's own loader are part of that game's loading and mean nothing.
- If it happens at the same place each time, lower the fast load speed for that game.

## The tape stops partway
- The status box says **Press Play for the next part**: the tape has stopped where the original was stopped. Click **Play** when the game asks for it.
- If the game is just waiting and asks for nothing, click **Play** anyway. To stop this for that game, untick **Stop Tape Between Parts** in its right-click menu in Edit Mode. (VIC-20 games fast loaded in several parts always stop between parts.)

## A game asks for the next part but the tape has run past it
- Rewind to the start of that part and click **Play** when the game asks. To have the tape stop there by itself next time, tick **Stop Tape Between Parts** in the game's right-click menu in Edit Mode.

## Loading finishes but nothing happens
- Type **RUN** and press **RETURN** (on C16 / Plus/4, F6 does this).
- On C16 / Plus/4, if a fast-loaded game stays on one colour after the last block, set its Fast Load to Off.

## Fast Load says Unavailable
- The game has its own loader, has several parts, or doesn't start from BASIC. It loads at normal speed instead.
- Try **Convert to PRG**; this sometimes makes fast load possible.

## No pictures or info
- Downloads need Edit Mode on and an internet connection.
- Some games are listed under a different name. Check the **Possible Matches** window, or rename the game.
- If TheGamesDB reports too many requests, wait and try again later. The other sources still work.

## The Manual button is greyed out
- No manual is matched to this game yet. Use **Add Manual…** in Edit Mode.
"))

        _topics.Add(New HelpTopic("Commodore Keys", "
# Commodore Keys Quick Reference
## Commodore 64 and VIC-20
- **LOAD** then **RETURN**: load the next program from tape.
- **SHIFT + RUN/STOP**: load and run the next program from tape.
- **RUN** then **RETURN**: start a loaded program.
- **RUN/STOP + RESTORE**: stop a BASIC program and reset the screen.

## C16, C116 and Plus/4
- **LOAD"""",1** then **RETURN**: load the next program from tape.
- **F6** (SHIFT + F3): types RUN and starts the program.
- **RUN** then **RETURN**: start a loaded program.
- **RUN/STOP + RESET** button: break into the machine-code monitor. Press **X** and **RETURN** to go back to BASIC.
- The **RESET** button on its own restarts the machine.

> SHIFT + RUN/STOP on the C16 and Plus/4 loads from disk, not tape, so use LOAD"""",1 for tapes.
"))
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _bodyFont.Dispose()
            _boldFont.Dispose()
            _headingFont.Dispose()
            _subheadingFont.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
