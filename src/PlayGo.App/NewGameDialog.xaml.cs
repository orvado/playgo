using System.Windows;

namespace PlayGo.App;

public partial class NewGameDialog : Window
{
    public NewGameDialog(int currentSize, bool blackIsComputer, bool whiteIsComputer)
    {
        InitializeComponent();
        SizeCombo.SelectedIndex = currentSize switch
        {
            9 => 0,
            13 => 1,
            _ => 2,
        };

        if (blackIsComputer && whiteIsComputer) ModeAIvAI.IsChecked = true;
        else if (blackIsComputer) ModeVsAIWhite.IsChecked = true;
        else if (whiteIsComputer) ModeVsAI.IsChecked = true;
        else ModePvP.IsChecked = true;
    }

    public int BoardSize => SizeCombo.SelectedIndex switch
    {
        0 => 9,
        1 => 13,
        _ => 19,
    };

    public bool BlackIsComputer => ModeVsAIWhite.IsChecked == true || ModeAIvAI.IsChecked == true;

    public bool WhiteIsComputer => ModeVsAI.IsChecked == true || ModeAIvAI.IsChecked == true;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
