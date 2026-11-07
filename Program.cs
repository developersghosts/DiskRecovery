namespace DiskRecovery;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // لقراءة مخرجات chkdsk بترميز الكونسول (OEM)
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        Application.Run(new UI.MainForm());
    }
}
