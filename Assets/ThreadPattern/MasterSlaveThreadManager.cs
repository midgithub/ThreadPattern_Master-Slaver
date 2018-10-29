/// <summary>
/// author:jiepengtan 
/// 
/// 约定：
/// 所有Blocking命名开头的函数 都可能导致线程阻塞 不应该主线程使用
/// 所有NoBlocking命名开头的函数 一定不会导致线程阻塞 可以用于主线程使用
/// 
/// 使用方式：
/// Task：如果希望自己的Task 可以被多遍遍历 可以重载  MaxPhaseNum() { return 1; } //返回希望被处理的次数
/// MasterSlaveThreadManager：一般只要重载
///    protected virtual void OnDealTaskPreparePart(Task _task) { }
///    protected virtual void OnDealTaskCommitPart(Task _task) { }
///    函数即可
///    OnDealTaskPreparePart 可能在任何线程被调用
///    OnDealTaskCommitPart 只可能在主线程调用
///   
/// //建议 
/// 使用：Thread local storage 请 继承ThreadWorker后添加相应的成员 并重写ResetThreadLoacalStorage
/// 使用：Task local storage 请继承Task后添加相应的成员
/// 
/// 具体使用方式可以参考：TestMutilPhaseThreadTaskManager
/// </summary>

//#define TEST_MAIN_THRED_BUG // 测试主线程参与是否会导致bug
//#define DEBUG_THREAD_PHASE_ID //开启线程阶段调试
using UnityEngine;
using System.Collections.Generic;
using System.Threading;
using System;


namespace FM.Threading {

    /// <summary>
    /// Master Slave 模式的线程管理器
    /// </summary>
    public class MasterSlaveThreadManager : MutilPhaseThreadTaskManager {

    }

}