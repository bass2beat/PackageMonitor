using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace IntuneLogMonitor.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Formatiert Bytezahlen in Bindungen ("1,20 GB").</summary>
public sealed class BytesConverter : System.Windows.Data.IValueConverter
{
    public static BytesConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is long l ? IntuneLogMonitor.Core.Formatting.FormatBytes(l) : "";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>
/// ObservableCollection mit Massenoperationen. Große Mengen werden mit einem einzigen Reset gemeldet
/// statt mit tausenden Einzel-Benachrichtigungen (WPF-CollectionViews unterstützen keine Bereichs-Adds).
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private const int ResetThreshold = 64;

    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;
        if (items.Count < ResetThreshold)
        {
            foreach (var item in items)
                Add(item);
            return;
        }
        CheckReentrancy();
        var list = (List<T>)Items;
        list.AddRange(items);
        RaiseReset();
    }

    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Count);
        if (count <= 0)
            return;
        CheckReentrancy();
        ((List<T>)Items).RemoveRange(0, count);
        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var list = (List<T>)Items;
        list.Clear();
        list.AddRange(items);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
