using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System;

namespace FM.Threading {

    public class Task : System.IComparable<Task> {

        public Task(System.Object param) {
            createTicks = System.DateTime.Now.Ticks;
            currentParseIdx = 0;
            this.Param = param;
        }
        public ThreadWorker threadLocalStorage;

        public System.Object Param { get; private set; }
        /// <summary>
        /// 创建的Tick用于标志是否过期
        /// </summary>
        public long createTicks { get; protected set; }
        /// <summary>
        /// 当前任务阶段
        /// </summary>
        public int currentParseIdx { get; protected set; }

#if TEST_MAIN_THRED_BUG
        public bool isDealInMainThread;
        public List<int> workedThreadIds = new System.Collections.Generic.List<int>();
#endif

        public bool IsFinished {
            get {
                if (IsInterrupted || Exception != null) {
                    return true;
                }
                return currentParseIdx >= MaxPhaseNum;
            }
        }
        public volatile bool IsInterrupted;
        public volatile System.Exception Exception;


        public System.Action<Task> OnFinishedEvent;
        public System.Action<Task> OnInterruptEvent;
        public bool IsMustRunInCurFrame = false;

        /// <summary>
        /// 当前阶段任务在那种类型的线程中执行
        /// </summary>
        /// <returns></returns>
        public virtual EThreadContext GetTargetQueueType() {
            if (IsMustRunInCurFrame) {
                return EThreadContext.MainThreadAndInCurFrame;
            } else {
                return EThreadContext.MainThread;//除了第一阶段由任务本身决定价格外，其他的都是在主线程中
            }
        }
        /// <summary>
        /// 任务被中断 回调，视情况是否需要重新提交任务
        /// </summary>
        public virtual void OnInterrupted() {
            if (OnInterruptEvent != null) OnInterruptEvent(this);
        }
        /// <summary>
        /// 可能失败  可能异常  可能成功 三种情况 回调
        /// </summary>
        public virtual void OnFinished() {
            if (OnFinishedEvent != null) OnFinishedEvent(this);
        }

        public void MoveNextPhase() { ++currentParseIdx; }

        protected virtual int MaxPhaseNum { get { return 1; } }
        protected virtual int CompareFunc(Task _other) {
            return _other.currentParseIdx - currentParseIdx;
        }

        int System.IComparable<Task>.CompareTo(Task _other) {
            return CompareFunc(_other);
        }
    }

}
