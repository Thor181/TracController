using System.Text;

namespace TracController;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\TracController.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("TracController уже запущен — ищите значок в трее.", "TracController",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // trac.exe пишет в OEM-кодировке консоли (cp866 на русской Windows)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
