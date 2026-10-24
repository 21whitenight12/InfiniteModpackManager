using System.Windows.Controls;

namespace Imm.App.Views;

/// <summary>
/// Scanner section: what IMM detects in each pack right now vs what its manifest claims,
/// plus every ambiguity the scanner reported (IMM.md: no silent guesses).
/// </summary>
public partial class ScannerView : UserControl
{
    public ScannerView() => InitializeComponent();
}
