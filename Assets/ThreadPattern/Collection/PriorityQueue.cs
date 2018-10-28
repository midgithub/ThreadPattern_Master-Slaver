using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;

namespace FM.Threading {
    /// <summary>
    /// 堆实现的优先队列
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class PriorityQueue<T> : IQueue<T> where T : IComparable<T> {
        List<T> array = new List<T>();
        public void Enqueue(T _item) {
            int _n = array.Count;
            array.Add(_item);
            while (_n != 0) {
                int _p = _n / 2;    // This is the 'parent' of this item
                if (array[_n].CompareTo(array[_p]) >= 0) break;  // Item >= parent
                T _tmp = array[_n]; array[_n] = array[_p]; array[_p] = _tmp; // Swap item and parent
                _n = _p;            // And continue
            }
        }
        public T Dequeue() {
            T _val = array[0];
            int _nMax = array.Count - 1;
            array[0] = array[_nMax]; array.RemoveAt(_nMax);  // Move the last element to the top
            int _p = 0;
            while (true) {
                int _c = _p * 2; if (_c >= _nMax) break;
                if (_c + 1 < _nMax && array[_c + 1].CompareTo(array[_c]) < 0) _c++;
                if (array[_p].CompareTo(array[_c]) <= 0) break;
                T _tmp = array[_p]; array[_p] = array[_c]; array[_c] = _tmp;
                _p = _c;
            }
            return _val;
        }
        public T Peek() {
            return array[0];
        }
        public void Clear() { array.Clear(); }
        public int Count {
            get { return array.Count; }
        }
        public bool Empty {
            get { return array.Count == 0; }
        }
    }
}
