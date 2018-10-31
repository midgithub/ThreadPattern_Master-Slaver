using UnityEngine;
using System.Collections;

namespace FM.Threading {

    public class ThreadWorker {
        public ThreadWorker(ThradPartternMasterSlaver _master) {
            master = _master;
        }
        private ThradPartternMasterSlaver master;
        /// <summary>
        /// reset thread local storage
        /// </summary>
        public virtual void ResetThreadLoacalStorage() { }
        public bool IsFinisedRun = false;
        public void Run() {
            try {
                master.DealQueue(master.AnyThreadTasks, this, null, false);
            } catch (System.Threading.ThreadInterruptedException) {
                //正常结束
                IsFinisedRun = true;
                Debug.Log("Interrupt Stop");
                return;
            }
            IsFinisedRun = true;
            Debug.Log("Auto Stop");
        }

    }

}
