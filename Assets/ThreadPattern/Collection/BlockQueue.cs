//=====================================================
// - FileName:      BlockQueue.cs
// - Created:       #AuthorName#
// - UserName:      #CreateTime#
// - Email:         #AuthorEmail#
//======================================================

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Threading;

namespace FM.Threading {

    public class QueueWrap<T> : IQueue<T> {
        private Queue<T> _proxy = new Queue<T>();
        public void Enqueue(T _ret) { _proxy.Enqueue(_ret); }
        public T Dequeue() { return _proxy.Dequeue(); }
        public int Count { get { return _proxy.Count; } }
    }

    public class BlockingQueue<T> : IBlockQueue<T> {
        private readonly IQueue<T> queue;
        private readonly int maxSize;
        //是否正在关闭
        public BlockingQueue(IQueue<T> _queue, int _maxSize) {
            this.maxSize = _maxSize;
            queue = _queue;
        }
        public bool IsEmpty {
            get {
                lock (queue) {
                    return queue.Count == 0;
                }
            }
        }
        public int Count {
            get {
                lock (queue) {
                    return queue.Count;
                }
            }
        }
        public bool IsClosing { get; private set; }

        public void Close() {
            lock (queue) {
                IsClosing = true;
                Monitor.PulseAll(queue);
            }
        }
        public void Enqueue(T item) {
            lock (queue) {
                while (queue.Count >= maxSize) {
                    Monitor.Wait(queue);
                    if (IsClosing) {
                        return;
                    }
                }
                queue.Enqueue(item);
                if (queue.Count == 1) {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(queue);
                }
            }
        }
        public T Dequeue() {
            lock (queue) {
                while (queue.Count == 0) {
                    Monitor.Wait(queue);
                    if (IsClosing) {
                        return default(T);
                    }
                }
                T _item = queue.Dequeue();
                if (queue.Count == maxSize - 1) {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(queue);
                }
                return _item;
            }
        }

        public bool TryDequeue(out T _val) {
            lock (queue) {
                if (queue.Count == 0) {
                    _val = default(T);
                    return false;
                }
                _val = queue.Dequeue();
                if (queue.Count == maxSize - 1) {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }
        public bool TryDequeue(out T _val, int _durationMs) {
            DateTime _deadline = DateTime.Now.AddMilliseconds(_durationMs);
            var _remainMs = _durationMs;
            lock (queue) {
                while (queue.Count == 0 && _remainMs > 0) {
                    if (IsClosing) {
                        _val = default(T);
                        return false;
                    }
                    Monitor.Wait(queue, _remainMs);
                    _remainMs = (int)(_deadline - DateTime.Now).TotalMilliseconds;
                }
                if (queue.Count == 0) {
                    _val = default(T);
                    return false;
                }
                _val = queue.Dequeue();
                if (queue.Count == maxSize - 1) {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }
        public bool TryEnqueue(T _item) {
            lock (queue) {
                if (queue.Count >= maxSize) {
                    return false;
                }
                queue.Enqueue(_item);
                if (queue.Count == 1) {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }

        public bool TryEnqueue(T _item, int _durationMs) {
            DateTime deadline = DateTime.Now.AddMilliseconds(_durationMs);
            var _remainMs = _durationMs;
            lock (queue) {
                while (queue.Count >= maxSize && _remainMs > 0) {
                    Monitor.Wait(queue, _remainMs);
                    _remainMs = (int)(deadline - DateTime.Now).TotalMilliseconds;
                }
                if (queue.Count >= maxSize) {
                    _item = default(T);
                    return false;
                }
                queue.Enqueue(_item);
                if (queue.Count == 1) {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }

    }
}
