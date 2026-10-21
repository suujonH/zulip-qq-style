using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ZulipQQ.Core;

/// <summary>
/// 把 <see cref="ObservableCollection{T}"/> 就地同步为目标列表：只做必要的删除、移动和插入，
/// 已有的元素对象保持不变，界面不会因整体重建而丢失选中、展开或滚动状态。
/// </summary>
public static class CollectionSync
{
    /// <summary>
    /// 同步后 <paramref name="target"/> 与 <paramref name="desired"/> 按引用逐项相同。
    /// 元素按引用比较，<paramref name="desired"/> 中不能有重复的对象。
    /// </summary>
    public static void SyncTo<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var desiredSet = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;

            var oldIndex = IndexOfReference(target, desired[i], i);
            if (oldIndex >= 0)
                target.Move(oldIndex, i);
            else
                target.Insert(i, desired[i]);
        }
    }

    // 不用 IndexOf：元素重写了 Equals 时可能找到另一个“相等”的对象
    private static int IndexOfReference<T>(ObservableCollection<T> target, T item, int start) where T : class
    {
        for (var i = start; i < target.Count; i++)
        {
            if (ReferenceEquals(target[i], item))
                return i;
        }
        return -1;
    }
}
