using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Stanza.App.Services;

namespace Stanza.App.ViewModels;

/// <summary>最近文件列表中的一项（视图展示用）。</summary>
public sealed class RecentFileItem
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>最近打开的 stanza 文件列表（MRU，新的在前），持久化由 <see cref="RecentFilesStore"/> 负责。</summary>
public sealed class RecentFilesViewModel : ViewModelBase
{
    private readonly Action<string> _openFile;
    private readonly Action<string> _notifyMissing;
    private string? _lastFile;

    /// <param name="openFile">打开指定文件的回调（由宿主提供）。</param>
    /// <param name="notifyMissing">文件已不存在时的提示回调。</param>
    public RecentFilesViewModel(Action<string> openFile, Action<string> notifyMissing)
    {
        _openFile = openFile;
        _notifyMissing = notifyMissing;

        OpenCommand = new RelayCommand(p => { if (p is string path) Open(path); });
        RemoveCommand = new RelayCommand(p => { if (p is string path) Remove(path); });

        var state = RecentFilesStore.Load();
        _lastFile = state.LastFile;
        foreach (var path in state.RecentFiles)
            Items.Add(new RecentFileItem { Path = path });
    }

    public ObservableCollection<RecentFileItem> Items { get; } = new();

    /// <summary>最近一次成功打开的文件（用于启动恢复）。</summary>
    public string? LastFile => _lastFile;

    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }

    /// <summary>文件成功打开/另存后登记到列表顶部。</summary>
    public void Register(string path)
    {
        _lastFile = path;
        Remove(path);
        Items.Insert(0, new RecentFileItem { Path = path });
        while (Items.Count > RecentFilesStore.MaxRecent)
            Items.RemoveAt(Items.Count - 1);
        Persist();
    }

    public void Remove(string path)
    {
        var existing = Items.FirstOrDefault(
            r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Items.Remove(existing);
            Persist();
        }
    }

    private void Open(string path)
    {
        if (File.Exists(path))
        {
            _openFile(path);
        }
        else
        {
            Remove(path);
            _notifyMissing(path);
        }
    }

    private void Persist()
        => RecentFilesStore.Save(new RecentState
        {
            LastFile = _lastFile,
            RecentFiles = Items.Select(r => r.Path).ToList(),
        });
}
