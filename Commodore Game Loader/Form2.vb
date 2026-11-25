Imports System.IO

' Settings for one system, on tabs:
'   Folders      - where games, images, manuals and info are kept. Leaving a box blank
'                  uses that system's own folder in the app folder (for example C64\Games).
'                  A folder chosen here is used exactly as it is.
'   Tape Options - signal settings for this system only.
'
' settings.txt lines: 0 games, 1 images, 2 manuals, 3 remember last game, 4 last game,
' then InfoFolder= and the per-game lines, which are kept when saving.
Public Class SettingsForm
    Private Const INFO_FOLDER_PREFIX As String = "InfoFolder="
    Private Const OLD_COPY_GAMES_PREFIX As String = "CopyGames=" ' from v1.6 test builds, dropped on save

    Public Property SettingsFilePath As String = Path.Combine(Application.StartupPath, "C64", "Settings", "settings.txt")
    Public Property SystemName As String = "Commodore 64"
    Public Property SystemFolder As String = Path.Combine(Application.StartupPath, "C64")

    ' Tape options: Form1 sets these before showing the form and reads them back after
    Public Property InvertSignal As Boolean = False
    Public Property SineWave As Boolean = False
    Public Property LeaderPercent As Integer = 100
    Private Shared ReadOnly LEADER_CHOICES As Integer() = {100, 50, 25}
    Public Property Saved As Boolean = False

    Private TxtInfoFolder As TextBox
    Private TxtTheGamesDBKey As TextBox

    ' The TheGamesDB key lives in one file beside the app, for every system
    Private Shared ReadOnly Property TheGamesDBKeyPath As String
        Get
            Return Path.Combine(Application.StartupPath, "thegamesdb_key.txt")
        End Get
    End Property

    Public Shared Function ReadTheGamesDBKey() As String
        Try
            If File.Exists(TheGamesDBKeyPath) Then Return File.ReadAllText(TheGamesDBKeyPath).Trim()
        Catch ex As IOException
            Debug.WriteLine($"Could not read the TheGamesDB key: {ex.Message}")
        End Try
        Return ""
    End Function
    Private ChkInvert As CheckBox
    Private ChkSine As CheckBox
    Private CmbLeader As ComboBox
    Private _existingLines As String() = {}

    Private Sub SettingsForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Me.Text = $"Settings - {SystemName}"
        Me.Icon = Form1.Icon
        AddInfoRow()
        BuildTabs()

        Label1.Text = $"Folders for {SystemName}. Leave a box blank to use the {Path.GetFileName(SystemFolder)} folder in the app folder."
        TxtGamesFolder.PlaceholderText = Path.Combine(SystemFolder, "Games")
        TxtScreenshotsFolder.PlaceholderText = Path.Combine(SystemFolder, "Images")
        TxtManualsFolder.PlaceholderText = Path.Combine(SystemFolder, "Manuals")
        TxtInfoFolder.PlaceholderText = Path.Combine(SystemFolder, "Info")
        ChkRememberLastGame.Checked = True
        ChkInvert.Checked = InvertSignal
        ChkSine.Checked = SineWave
        CmbLeader.SelectedIndex = Math.Max(0, Array.IndexOf(LEADER_CHOICES, LeaderPercent))

        If File.Exists(SettingsFilePath) Then
            _existingLines = File.ReadAllLines(SettingsFilePath)
            If _existingLines.Length > 0 Then TxtGamesFolder.Text = _existingLines(0)
            If _existingLines.Length > 1 Then TxtScreenshotsFolder.Text = _existingLines(1)
            If _existingLines.Length > 2 Then TxtManualsFolder.Text = _existingLines(2)
            Dim rememberLastGame As Boolean
            If _existingLines.Length > 3 AndAlso Boolean.TryParse(_existingLines(3), rememberLastGame) Then
                ChkRememberLastGame.Checked = rememberLastGame
            End If
            Dim infoLine As String = _existingLines.Skip(5).FirstOrDefault(Function(l) l.StartsWith(INFO_FOLDER_PREFIX))
            If infoLine IsNot Nothing Then TxtInfoFolder.Text = infoLine.Substring(INFO_FOLDER_PREFIX.Length)
        End If
    End Sub

    ' The designer has no Info row, so one is added below Screenshots
    Private Sub AddInfoRow()
        Dim rowGap As Integer = TxtScreenshotsFolder.Top - TxtManualsFolder.Top
        Dim infoLabel As New Label() With {.Text = "Info", .AutoSize = True,
            .Location = New Point(LblScreenshotsFolder.Left, LblScreenshotsFolder.Top + rowGap)}
        TxtInfoFolder = New TextBox() With {.Size = TxtScreenshotsFolder.Size,
            .Location = New Point(TxtScreenshotsFolder.Left, TxtScreenshotsFolder.Top + rowGap)}
        Dim browseInfo As New Button() With {.Text = "Browse", .Size = BtnBrowseScreenshots.Size,
            .Location = New Point(BtnBrowseScreenshots.Left, BtnBrowseScreenshots.Top + rowGap)}
        AddHandler browseInfo.Click, Sub(s, args) BrowseInto(TxtInfoFolder)

        ChkRememberLastGame.Top = TxtInfoFolder.Bottom + 18
        Me.Controls.AddRange(New Control() {infoLabel, TxtInfoFolder, browseInfo})
    End Sub

    ' Moves the designer's controls onto a Folders tab and adds a Tape Options tab
    Private Sub BuildTabs()
        Dim tabs As New TabControl() With {.Location = New Point(8, 8)}
        Dim foldersPage As New TabPage("Folders") With {.UseVisualStyleBackColor = True}
        Dim tapePage As New TabPage("Tape Options") With {.UseVisualStyleBackColor = True}

        Dim contentBottom As Integer = 0
        Dim contentRight As Integer = 0
        For Each item As Control In Me.Controls.Cast(Of Control)().ToList()
            If item Is BtnSave Then Continue For
            contentBottom = Math.Max(contentBottom, item.Bottom)
            contentRight = Math.Max(contentRight, item.Right)
            Me.Controls.Remove(item)
            foldersPage.Controls.Add(item)
        Next

        ChkInvert = New CheckBox() With {.Text = "Invert signal (try this if a game will not load)", .AutoSize = True, .Location = New Point(23, 24)}
        ChkSine = New CheckBox() With {.Text = "Sine wave output instead of square", .AutoSize = True, .Location = New Point(23, 56)}
        Dim leaderLabel As New Label() With {.Text = "Short leader (test):", .AutoSize = True, .Location = New Point(23, 92)}
        CmbLeader = New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 140, .Location = New Point(150, 88)}
        CmbLeader.Items.AddRange({"Normal", "Half", "Quarter"})
        Dim leaderNote As New Label() With {
            .Text = "Shortens the tone before each standard file, so loading starts sooner." & Environment.NewLine &
                    "If loading becomes unreliable, set it back to Normal.",
            .AutoSize = True, .ForeColor = Color.DimGray, .Location = New Point(23, 118)}
        Dim tapeNote As New Label() With {
            .Text = $"These apply to the {SystemName} only. Each system keeps its own.",
            .AutoSize = True, .ForeColor = Color.DimGray, .Location = New Point(23, 164)}
        tapePage.Controls.AddRange(New Control() {ChkInvert, ChkSine, leaderLabel, CmbLeader, leaderNote, tapeNote})

        ' Sources: the TheGamesDB key, for more game info and pictures
        Dim sourcesPage As New TabPage("Sources") With {.UseVisualStyleBackColor = True}
        Dim keyLabel As New Label() With {.Text = "TheGamesDB API key:", .AutoSize = True, .Location = New Point(23, 24)}
        TxtTheGamesDBKey = New TextBox() With {.Location = New Point(23, 44), .Width = Math.Max(360, contentRight - 30),
                                               .UseSystemPasswordChar = True, .Text = ReadTheGamesDBKey()}
        Dim showKey As New CheckBox() With {.Text = "Show key", .AutoSize = True, .Location = New Point(23, 72)}
        AddHandler showKey.CheckedChanged, Sub(s, args) TxtTheGamesDBKey.UseSystemPasswordChar = Not showKey.Checked
        Dim keyNote As New Label() With {
            .Text = "Used for game info and pictures that libretro and Plus/4 World don't have," & Environment.NewLine &
                    "before Wikipedia. Get a free key from thegamesdb.net. It is kept on this PC only" & Environment.NewLine &
                    "(in thegamesdb_key.txt beside the app), never in the app's code.",
            .AutoSize = True, .ForeColor = Color.DimGray, .Location = New Point(23, 100)}
        Dim siteLink As New LinkLabel() With {.Text = "thegamesdb.net", .AutoSize = True, .Location = New Point(23, 156)}
        AddHandler siteLink.LinkClicked, Sub(s, args)
                                             Try
                                                 Process.Start(New ProcessStartInfo("https://thegamesdb.net") With {.UseShellExecute = True})
                                             Catch ex As Exception
                                                 Debug.WriteLine(ex.Message)
                                             End Try
                                         End Sub
        sourcesPage.Controls.AddRange(New Control() {keyLabel, TxtTheGamesDBKey, showKey, keyNote, siteLink})

        tabs.TabPages.Add(foldersPage)
        tabs.TabPages.Add(tapePage)
        tabs.TabPages.Add(sourcesPage)
        tabs.Size = New Size(contentRight + 32, contentBottom + 48)
        Me.Controls.Add(tabs)

        BtnSave.Location = New Point(tabs.Right - BtnSave.Width, tabs.Bottom + 10)
        Me.ClientSize = New Size(tabs.Right + 8, BtnSave.Bottom + 12)
        BtnSave.BringToFront()
    End Sub

    Private Sub BrowseInto(target As TextBox)
        Using folderDialog As New FolderBrowserDialog()
            If folderDialog.ShowDialog() = DialogResult.OK Then
                target.Text = folderDialog.SelectedPath
            End If
        End Using
    End Sub

    Private Sub BtnBrowseGames_Click(sender As Object, e As EventArgs) Handles BtnBrowseGames.Click
        BrowseInto(TxtGamesFolder)
    End Sub

    Private Sub BtnBrowseScreenshots_Click(sender As Object, e As EventArgs) Handles BtnBrowseScreenshots.Click
        BrowseInto(TxtScreenshotsFolder)
    End Sub

    Private Sub BtnBrowseManuals_Click(sender As Object, e As EventArgs) Handles BtnBrowseManuals.Click
        BrowseInto(TxtManualsFolder)
    End Sub

    Private Sub BtnSave_Click(sender As Object, e As EventArgs) Handles BtnSave.Click
        Dim lines As New List(Of String) From {
            TxtGamesFolder.Text.Trim(),
            TxtScreenshotsFolder.Text.Trim(),
            TxtManualsFolder.Text.Trim(),
            ChkRememberLastGame.Checked.ToString(),
            If(_existingLines.Length > 4, _existingLines(4), "")
        }
        If TxtInfoFolder.Text.Trim() <> "" Then lines.Add(INFO_FOLDER_PREFIX & TxtInfoFolder.Text.Trim())
        ' Per-game settings and anything else after the first five lines are kept
        lines.AddRange(_existingLines.Skip(5).Where(Function(l) Not l.StartsWith(INFO_FOLDER_PREFIX) AndAlso
                                                                 Not l.StartsWith(OLD_COPY_GAMES_PREFIX)))

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath))
        File.WriteAllLines(SettingsFilePath, lines)
        Try
            Dim key As String = If(TxtTheGamesDBKey?.Text, "").Trim()
            If key = "" Then
                If File.Exists(TheGamesDBKeyPath) Then File.Delete(TheGamesDBKeyPath)
            Else
                File.WriteAllText(TheGamesDBKeyPath, key)
            End If
        Catch ex As IOException
            MessageBox.Show($"Could not save the TheGamesDB key: {ex.Message}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try

        InvertSignal = ChkInvert.Checked
        SineWave = ChkSine.Checked
        LeaderPercent = LEADER_CHOICES(Math.Max(0, CmbLeader.SelectedIndex))
        Saved = True
        MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Me.Close()
    End Sub
End Class
