using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace YouTube
{
    // ObservableCollection raises one UI notification per Add. On old UWP ItemsWrapGrid this
    // can trigger dozens of measure/arrange passes for a single parsed page. Batch mutations
    // raise one Reset notification and are compatible with VS2015/C# 6 and .NET Native.
    public sealed class FastObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> values)
        {
            if (values == null)
            {
                return;
            }

            CheckReentrancy();
            var changed = false;
            foreach (var value in values)
            {
                Items.Add(value);
                changed = true;
            }

            if (changed)
            {
                RaiseReset();
            }
        }

        public void ReplaceAll(IEnumerable<T> values)
        {
            CheckReentrancy();
            Items.Clear();
            if (values != null)
            {
                foreach (var value in values)
                {
                    Items.Add(value);
                }
            }

            RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
        }
    }
}
