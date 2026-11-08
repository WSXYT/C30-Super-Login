using System.Windows.Forms;
internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string output = args[0]; string mode = args.Length > 1 ? args[1] : "normal";
        var user = new TextBox { Name = "loginname", Left = 24, Top = 24, Width = 330 };
        var password = new TextBox { Name = "loginpwd", Left = 24, Top = 68, Width = 330, UseSystemPasswordChar = mode != "unprotected" };
        var button = new Button { Name = "loginbtn", Text = "Submit local fixture", Left = 24, Top = 112, Width = 330 };
        var form = new FixtureForm { Text = "C30 owned UIA fixture", Width = 400, Height = 250, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-30000, -30000) };
        form.Controls.AddRange([user, password, button]);
        if (mode == "duplicate") form.Controls.Add(new TextBox { Name = "loginname", Left = 24, Top = 160, Width = 330 });
        int count = 0;
        if (mode == "changed-user") password.TextChanged += (_, _) => {
            user.Text = "LOCAL_UNEXPECTED_USER";
            File.WriteAllText(output + ".mutation", System.Text.Json.JsonSerializer.Serialize(new {
                synthetic_username_changed = user.Text == "LOCAL_UNEXPECTED_USER",
                synthetic_password_assigned = password.Text == "LOCAL_UIA_PASSWORD"
            }));
        };
        form.Shown += (_, _) => File.WriteAllText(output + ".window", form.Handle.ToInt64().ToString());
        button.Click += (_, _) => File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { submitted = ++count, synthetic_username_ok = user.Text == "LOCAL_UIA_USER", synthetic_password_ok = password.Text == "LOCAL_UIA_PASSWORD" }));
        Application.Run(form);
    }
    // Test windows must expose real UIA controls without stealing the user's focus.
    // The ready marker is published only after initial showing has completed.
    private sealed class FixtureForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } // WS_EX_NOACTIVATE
        }
    }
}
