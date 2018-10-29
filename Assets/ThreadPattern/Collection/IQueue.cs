using UnityEngine;
using System.Collections;

namespace FM.Threading {
    public interface IQueue<T> {
        T Dequeue();
        void Enqueue(T _ret);
        int Count { get; }
    }

    public interface IBlockQueue<T> {
        bool TryDequeue(out T _ret);
        bool TryDequeue(out T _ret, int _ms);
        bool TryEnqueue(T _ret);
        bool TryEnqueue(T _ret, int _ms);
        T Dequeue();
        void Enqueue(T _ret);
        bool IsEmpty { get; }
        int Count { get; }
        bool IsClosing { get; set; }
        bool Close();
    }
}

