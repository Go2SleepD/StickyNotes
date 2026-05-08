using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace StickerApp;

public class TaskItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string prop) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; Notify(nameof(Text)); }
    }

    private bool _done;
    public bool Done
    {
        get => _done;
        set { if (_done == value) return; _done = value; Notify(nameof(Done)); }
    }

    [JsonIgnore]
    private bool _isEditing;
    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing == value) return; _isEditing = value; Notify(nameof(IsEditing)); }
    }

    [JsonIgnore]
    private string _displayText = "";
    [JsonIgnore]
    public string DisplayText
    {
        get => _displayText;
        set { if (_displayText == value) return; _displayText = value; Notify(nameof(DisplayText)); }
    }
}

public class StickerData
{
    public string Id          { get; set; } = Guid.NewGuid().ToString();
    public string Title       { get; set; } = "Tasks";
    public string AccentColor { get; set; } = "#5E6AD2";
    public double X           { get; set; }
    public double Y           { get; set; }
    public double Width       { get; set; } = 260;
    public double Height      { get; set; } = 260;
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public int      ResetCount  { get; set; } = 0;
    public string?  OutlineColor { get; set; }
    public bool     IsRule       { get; set; }
    public bool     IsActive     { get; set; } = true;
    public ObservableCollection<TaskItem> Tasks { get; set; } = [];
}
