' About box, laid out in code as one centred column so nothing sits off centre.
' The designer's labels are replaced when the form opens.
Public Class AboutForm
    Private Const WEBSITE As String = "https://nyimski.net"
    Private Const EMAIL As String = "nyimski@nyimski.net"
    Private Const SOURCE_CODE As String = "https://github.com/Nyimski"

    Private _linkHandled As Boolean = False ' Flag to prevent multiple openings

    Private Sub AboutForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Me.Text = "About Commodore Game Loader"
        Me.Icon = Form1.Icon
        Me.FormBorderStyle = FormBorderStyle.FixedDialog
        Me.MaximizeBox = False
        Me.MinimizeBox = False
        Me.StartPosition = FormStartPosition.CenterParent
        Me.BackColor = Color.White

        Me.Controls.Clear()

        Dim column As New TableLayoutPanel() With {
            .ColumnCount = 1,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .Padding = New Padding(36, 28, 36, 24),
            .Location = New Point(0, 0)
        }
        column.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))

        ' Each item is centred in the column, which is as wide as the widest item
        Dim addCentred = Sub(item As Control, gapBelow As Integer)
                             item.Anchor = AnchorStyles.None
                             item.Margin = New Padding(0, 0, 0, gapBelow)
                             column.Controls.Add(item)
                         End Sub

        Dim logo As New PictureBox() With {
            .Size = New Size(64, 64),
            .SizeMode = PictureBoxSizeMode.Zoom,
            .Image = New Icon(Form1.Icon, New Size(64, 64)).ToBitmap()
        }
        addCentred(logo, 12)

        addCentred(New Label() With {.Text = "Commodore Game Loader", .AutoSize = True,
                                     .Font = New Font("Segoe UI", 16.0F, FontStyle.Bold)}, 2)
        addCentred(New Label() With {.Text = "Version " & Form1.APP_VERSION, .AutoSize = True,
                                     .Font = New Font("Segoe UI", 10.0F), .ForeColor = Color.DimGray}, 16)
        addCentred(New Label() With {
            .Text = "Plays Commodore 64, VIC-20 and C16 / Plus/4 tape games" & vbCrLf &
                    "from your PC to the real machine, with fast loading" & vbCrLf &
                    "for compatible games.",
            .AutoSize = True, .TextAlign = ContentAlignment.MiddleCenter,
            .Font = New Font("Segoe UI", 10.0F)}, 6)
        addCentred(New Label() With {.Text = "Developed by Nyimski", .AutoSize = True,
                                     .Font = New Font("Segoe UI", 10.0F, FontStyle.Italic)}, 18)

        addCentred(MakeLink("Website:  ", WEBSITE.Replace("https://", ""), AddressOf OpenWebsite), 4)
        addCentred(MakeLink("Contact:  ", EMAIL, AddressOf SendEmail), 4)
        addCentred(MakeLink("Source code:  ", "GitHub", AddressOf OpenGitHub), 18)

        addCentred(New Label() With {.Text = "Game info and pictures from libretro thumbnails, Plus/4 World, The Commodore Zone, TheGamesDB and Wikipedia",
                                     .AutoSize = True, .Font = New Font("Segoe UI", 8.5F), .ForeColor = Color.DimGray}, 4)
        addCentred(New Label() With {.Text = "Licensed under the GNU General Public License v3.0 (GPLv3)",
                                     .AutoSize = True, .Font = New Font("Segoe UI", 8.5F), .ForeColor = Color.DimGray}, 16)

        Dim closeButton As New Button() With {.Text = "Close", .Size = New Size(96, 30), .DialogResult = DialogResult.OK}
        addCentred(closeButton, 0)
        Me.AcceptButton = closeButton
        Me.CancelButton = closeButton

        Me.Controls.Add(column)
        Me.AutoSize = True
        Me.AutoSizeMode = AutoSizeMode.GrowAndShrink
    End Sub

    ' A line such as "Website: nyimski.net" where only the second part is a link
    Private Function MakeLink(caption As String, linkText As String,
                              handler As LinkLabelLinkClickedEventHandler) As LinkLabel
        Dim link As New LinkLabel() With {
            .Text = caption & linkText,
            .AutoSize = True,
            .Font = New Font("Segoe UI", 10.0F),
            .LinkArea = New LinkArea(caption.Length, linkText.Length)
        }
        AddHandler link.LinkClicked, handler
        Return link
    End Function

    Private Sub OpenAddress(address As String, failure As String)
        If _linkHandled Then Return
        _linkHandled = True
        Try
            Process.Start(New ProcessStartInfo(address) With {.UseShellExecute = True, .Verb = "open"})
        Catch ex As Exception
            MessageBox.Show(failure & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            _linkHandled = False
        End Try
    End Sub

    Private Sub OpenWebsite(sender As Object, e As LinkLabelLinkClickedEventArgs)
        OpenAddress(WEBSITE, "Unable to open website: ")
    End Sub

    Private Sub SendEmail(sender As Object, e As LinkLabelLinkClickedEventArgs)
        OpenAddress("mailto:" & EMAIL, "Unable to open email client: ")
    End Sub

    Private Sub OpenGitHub(sender As Object, e As LinkLabelLinkClickedEventArgs)
        OpenAddress(SOURCE_CODE, "Unable to open GitHub: ")
    End Sub
End Class
