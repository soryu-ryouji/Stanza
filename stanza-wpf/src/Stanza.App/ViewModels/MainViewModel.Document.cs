using System.IO;
using System.Text;
using Stanza.App.Services;
using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>
/// 文档生命周期：打开/新建/保存与脏追踪。序列化统一走 SerializeDocument（VM → StanzaDocument
/// → StanzaWriter.Write），是撤销快照与保存共用的唯一路径；空区块按 ExistedInSource 决定是否写回（§6.3）。
/// </summary>
public sealed partial class MainViewModel
{
    // ---- 文件 ----

    public void OpenFile(string path)
    {
        if (!FlushDirty()) return;
        try
        {
            var doc = StanzaParser.Parse(File.ReadAllText(path));
            LoadDocument(doc);
            FilePath = path;
            FileName = Path.GetFileName(path);
            HasDocument = true;
            IsDirty = false;
            Recents.Register(path);
            SetStatus(doc.Warnings.Count > 0
                ? SaveStatus.Info
                : SaveStatus.None,
                doc.Warnings.Count > 0 ? Loc.Format("Status_Warnings", doc.Warnings.Count) : "");
        }
        catch (Exception ex)
        {
            SetStatus(SaveStatus.Error, Loc.Format("Status_OpenFailed", ex.Message));
        }
    }

    private void OpenInteractive()
    {
        var path = PickOpenFile?.Invoke();
        if (!string.IsNullOrEmpty(path)) OpenFile(path);
    }

    /// <summary>启动时恢复上次打开的文件；没有则停留在欢迎页。</summary>
    public void OpenStartupFile()
    {
        if (Recents.LastFile is { } last && File.Exists(last))
            OpenFile(last);
    }

    public void NewDocument()
    {
        if (!FlushDirty()) return;

        _suppressDirty = true;
        try
        {
            _undoStack.Clear();
            Blocks.Clear();
            foreach (var state in TaskStateNames.CanonicalOrder)
                Blocks.Add(CreateBlock(state, existedInSource: true));
            SelectedBlock = Blocks[0];
            SelectedTask = null;
            CollapseExpanded();
            RefreshFacets();
            FilePath = null;
            FileName = Loc.Get("File_Untitled");
            HasDocument = true;
            IsDirty = false;
            SetStatus(SaveStatus.None, "");
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    public void Save()
    {
        if (!HasDocument) return;
        _autoSaveTimer.Stop();

        if (FilePath == null)
        {
            var path = PickSaveFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;   // 用户取消，保持未保存状态
            FilePath = path;
            FileName = Path.GetFileName(path);
            Recents.Register(path);
        }

        try
        {
            SetStatus(SaveStatus.Saving, Loc.Get("Status_Saving"));
            File.WriteAllText(FilePath, SerializeDocument(), new UTF8Encoding(false));
            // 本次写出后这些区块已存在于源文件（§6.3），之后变空也要写回区块头
            foreach (var b in Blocks)
                if (!b.ExistedInSource && b.Tasks.Any(t => !t.IsEmpty))
                    b.ExistedInSource = true;
            IsDirty = false;
            SetStatus(SaveStatus.Saved, Loc.Format("Status_Saved", DateTime.Now.ToString("HH:mm")));
        }
        catch (Exception ex)
        {
            SetStatus(SaveStatus.Error, Loc.Format("Status_SaveFailed", ex.Message));
        }
    }

    /// <summary>当前文档的规范序列化文本（Save 与撤销快照共用的唯一序列化路径）。</summary>
    private string SerializeDocument()
    {
        var doc = new StanzaDocument();
        foreach (var b in Blocks)
        {
            var models = b.Tasks.Where(t => !t.IsEmpty).Select(t => t.ToModel()).ToList();
            // 空区块仅在源文件中存在时才写回（§6.3）
            if (models.Count == 0 && !b.ExistedInSource) continue;
            var block = doc.GetOrAddBlock(b.State);
            block.Tasks.AddRange(models);
        }
        return StanzaWriter.Write(doc);
    }


    private bool FlushDirty()
    {
        if (!IsDirty) return true;
        Save();
        return !IsDirty;
    }

    // ---- 变更通知 ----

    public void NotifyContentChanged()
    {
        if (_suppressDirty) return;
        IsDirty = true;
        SetStatus(SaveStatus.Dirty, Loc.Get("Status_Dirty"));
        // 新文档尚无路径，等用户显式 Ctrl+S 再弹保存对话框
        if (FilePath != null)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }
}
