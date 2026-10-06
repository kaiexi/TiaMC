using System.Windows;
using System.Windows.Input;
using TiaMc.App.Mvvm;

namespace TiaMc.App.ViewModels;

/// <summary>Drives the Microsoft device code dialog (code display + live status).</summary>
public sealed class DeviceCodeViewModel : ObservableObject
{
    private string _userCode = "...";
    private string _verificationUri = "https://www.microsoft.com/link";
    private string _status = "正在申请设备代码...";
    private string _detail = "";
    private bool _waiting = true;
    private bool _finished;
    private bool _failed;
    private int _secondsLeft;

    public string UserCode
    {
        get => _userCode;
        set => Set(ref _userCode, value);
    }

    public string VerificationUri
    {
        get => _verificationUri;
        set => Set(ref _verificationUri, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public bool IsWaiting
    {
        get => _waiting;
        set => Set(ref _waiting, value);
    }

    public bool IsFinished
    {
        get => _finished;
        set => Set(ref _finished, value);
    }

    public bool IsFailed
    {
        get => _failed;
        set => Set(ref _failed, value);
    }

    public int SecondsLeft
    {
        get => _secondsLeft;
        set
        {
            if (Set(ref _secondsLeft, value)) Raise(nameof(TimeText));
        }
    }

    public string TimeText => SecondsLeft > 0 ? $"剩余 {SecondsLeft / 60:00}:{SecondsLeft % 60:00}" : "";

    public string FullLink => $"{VerificationUri}  ·  代码 {UserCode}";

    public ICommand CopyCodeCommand => new RelayCommand(() =>
    {
        try
        {
            Clipboard.SetText(UserCode);
        }
        catch (Exception)
        {
            // Clipboard can be locked by another process; ignore.
        }
    });

    public ICommand OpenLinkCommand => new RelayCommand(() =>
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = VerificationUri,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Opening the browser is best effort.
        }
    });

    public void RaiseLinkChanged()
    {
        Raise(nameof(FullLink));
        Raise(nameof(TimeText));
    }
}
