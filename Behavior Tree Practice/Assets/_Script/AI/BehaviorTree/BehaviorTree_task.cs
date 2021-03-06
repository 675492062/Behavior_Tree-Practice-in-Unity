﻿

using System.Collections;
using System.Collections.Generic;


namespace behaviac
{

    public enum EBTStatus
    {
        BT_INVALID,
        BT_SUCCESS,
        BT_FAILURE,
        BT_RUNNING,
    }

    /**
    trigger mode to control the bt switching and back
    */
    public enum TriggerMode
    {
        TM_Transfer,
        TM_Return
    }

    ///return false to stop traversing
    public delegate bool NodeHandler_t(BehaviorTask task, Agent agent, object user_data);

    public abstract class BehaviorTask
    {

        public EBTStatus m_status;
        protected BehaviorNode m_node;
        protected BranchTask m_parent;
        protected List<AttachmentTask> m_attachments;

        private int m_id;

        private static EBTStatus ms_lastExitStatus_ = EBTStatus.BT_INVALID;

        static NodeHandler_t abort_handler_ = abort_handler;
        static NodeHandler_t reset_handler_ = reset_handler;


        public virtual void Init(BehaviorNode node)
        {
            this.m_node = node;

            int attachmentsCount = node.GetAttachmentsCount();
            if (attachmentsCount > 0)
            {
                for (int i = 0; i < attachmentsCount; i++)
                {
                    BehaviorNode attachmentNode = node.GetAttachment(i);
                    AttachmentTask attachmentTask = (AttachmentTask)attachmentNode.CreateAndInitTask();

                    this.Attach(attachmentTask);
                }
            }
        }

        private void Attach(AttachmentTask pAttachment)
        {
            if (this.m_attachments == null)
            {
                this.m_attachments = new List<AttachmentTask>();
            }

            this.m_attachments.Add(pAttachment);
        }

        public virtual void Clear()
        {
            this.m_status = EBTStatus.BT_INVALID;
            this.m_parent = null;

            if (this.m_attachments != null)
            {
                this.m_attachments.Clear();
                this.m_attachments = null;
            }

            this.m_node = null;
        }

        public static void DestroyTask(BehaviorTask task)
        {
            //nothing
        }

        /**
        return false if the event handling needs to be stopped

        an event can be configured to stop being checked if triggered
        */
        public bool CheckEvents(string eventName, Agent pAgent)
        {
            if (this.m_attachments != null)
            {
                //bool bTriggered = false;
                for (int i = 0; i < this.m_attachments.Count; ++i)
                {
                    AttachmentTask pA = this.m_attachments[i];
                    Event.EventTask pE = pA as Event.EventTask;

                    //check events only
                    if (pE != null && !string.IsNullOrEmpty(eventName))
                    {
                        string pEventName = pE.GetEventName();

                        if (!string.IsNullOrEmpty(pEventName) && pEventName == eventName)
                        {
                            EBTStatus currentStatus = pA.GetStatus();

                            if (currentStatus == EBTStatus.BT_RUNNING || currentStatus == EBTStatus.BT_INVALID)
                            {
                                currentStatus = pA.exec(pAgent);
                            }

                            if (currentStatus == EBTStatus.BT_SUCCESS)
                            {
                                //bTriggered = true;

                                if (pE.TriggeredOnce())
                                {
                                    return false;
                                }
                            }
                            else if (currentStatus == EBTStatus.BT_FAILURE)
                            {
                            }
                        }
                    }
                }
            }

            return true;
        }

        public virtual bool CheckPredicates(Agent pAgent)
        {
            if (this.m_attachments == null || this.m_attachments.Count == 0)
                return true;

            bool lastCombineValue = false;
            for (int i = 0; i < this.m_attachments.Count; ++i)
            {
                AttachmentTask attchment = this.m_attachments[i];
                Predicate.PredicateTask predicateTask = attchment as Predicate.PredicateTask;

                if (predicateTask != null)
                {
                    EBTStatus executeStatus = predicateTask.GetStatus();
                    if (executeStatus == EBTStatus.BT_RUNNING || executeStatus == EBTStatus.BT_INVALID)
                        executeStatus = predicateTask.exec(pAgent);

                    bool taskBoolean = getBooleanFromStatus(executeStatus);
                    if (i == 0)
                    {
                        lastCombineValue = taskBoolean;
                    }
                    else
                    {
                        bool andOp = predicateTask.IsAnd();
                        if (andOp)
                            lastCombineValue = lastCombineValue && taskBoolean;
                        else
                            lastCombineValue = lastCombineValue || taskBoolean;
                    }
                }
            }

            return lastCombineValue;
        }

        //CheckBreakpoint should be after log of onenter/onexit/update, as it needs to flush msg to the client
        private static void CHECK_BREAKPOINT(Agent pAgent, BehaviorTask b, string action, EActionResult actionResult)
        {
#if !BEHAVIAC_RELEASE
            if (Config.IsLoggingOrSocketing)
            {
                string bpstr = GetTickInfo(pAgent, b, action);
                if (!string.IsNullOrEmpty(bpstr))
                {
                    LogManager.Log(pAgent, bpstr, actionResult, LogMode.ELM_tick);
                    if (Config.IsDebugging)
                    {
                        if (Workspace.CheckBreakpoint(pAgent, b, action, actionResult))
                        {
                            LogManager.Log(pAgent, bpstr, actionResult, LogMode.ELM_breaked);
                            LogManager.Flush(pAgent);
                            SocketUtils.Flush();

                            _MY_BREAKPOINT_BREAK_(pAgent, bpstr, actionResult);

                            LogManager.Log(pAgent, bpstr, actionResult, LogMode.ELM_continue);
                            LogManager.Flush(pAgent);
                            SocketUtils.Flush();
                        }
                    }
                }
            }
#endif
        }

        private static void _MY_BREAKPOINT_BREAK_(Agent pAgent, string btMsg, EActionResult actionResult)
        {
#if !BEHAVIAC_RELEASE
            if (Config.IsLoggingOrSocketing)
            {
                string actionResultStr = GetActionResultStr(actionResult);
                string msg = string.Format("BehaviorTreeTask Breakpoints at: '{0}{1}'\n\nOk to continue.", btMsg, actionResultStr);

                Workspace.RespondToBreak(msg, "BehaviorTreeTask Breakpoints");
            }
#endif
        }

        public EBTStatus exec(Agent pAgent)
        {
#if !BEHAVIAC_RELEASE
            Debug.Check(this.m_node == null || this.m_node.IsValid(pAgent, this),
                string.Format("Agent In BT:{0} while the Agent used for: {1}", this.m_node.GetAgentType(), pAgent.GetClassTypeName()));
#endif//#if !BEHAVIAC_RELEASE

            bool bEnterResult = false;
            if (this.m_status == EBTStatus.BT_RUNNING)
            {
                bEnterResult = true;
            }
            else
            {
                //reset it to invalid when it was success/failure
                this.m_status = EBTStatus.BT_INVALID;

                bEnterResult = this.onenter_action(pAgent);

                //for continue ticking task, to set it as the cached current task
                bool bIsContinueTicking = this.isContinueTicking();
                if (bIsContinueTicking)
                {
                    BranchTask pBranch = this.GetParentBranch();

                    if (pBranch != null && pBranch != this)
                    {
                        //if 'this' is a tree, don't set it into it parent's current node
                        Debug.Check(!(this is BehaviorTreeTask));

                        pBranch.SetCurrentTask(this);
                    }
                }
            }

            if(bEnterResult)
            {
#if !BEHAVIAC_RELEASE
                if (Config.IsLoggingOrSocketing)
                {
                    string btStr = BehaviorTask.GetTickInfo(pAgent, this, "update");
                    //empty btStr is for internal BehaviorTreeTask
                    if (!string.IsNullOrEmpty(btStr))
                    {
                        LogManager.Log(pAgent, btStr, EActionResult.EAR_none, LogMode.ELM_tick);
                    }
                }
#endif

                bool bEnded = false;
                EBTStatus returnStatus = this.GetReturnStatus();
                if (returnStatus == EBTStatus.BT_INVALID)
                {
                    this.m_status = this.update(pAgent, EBTStatus.BT_RUNNING);
                }
                else
                {
                    this.m_status = returnStatus;
                    bEnded = true;
                }

                if (this.m_status != EBTStatus.BT_RUNNING)
                {
                    //clear it
                    bool bIsContinueTicking = this.isContinueTicking();
                    if (bIsContinueTicking)
                    {
                        BranchTask pBranch = this.GetParentBranch();

                        if (pBranch != null && pBranch != this)
                        {
                            //if 'this' is a tree, don't set it into it parent's current node
                            Debug.Check(!(this is BehaviorTreeTask));

                            pBranch.SetCurrentTask(null);
                        }
                    }

                    if (!bEnded)
                    {
                        this.onexit_action(pAgent, this.m_status);
                    }
                }
            }
            else
            {
                this.m_status = EBTStatus.BT_FAILURE;
            }

            EBTStatus currentStatus = this.m_status;
            if (this.m_status != EBTStatus.BT_RUNNING && this.NeedRestart())
            {
                //reset it to invalid when it needs restarting
                //don't need to reset the sub tree
                this.m_status = EBTStatus.BT_INVALID;
                this.SetReturnStatus(EBTStatus.BT_INVALID);
            }

            return currentStatus;
        }

        public virtual EBTStatus GetReturnStatus()
        {
            return EBTStatus.BT_INVALID;
        }

        /**
        a branch is a node whose isContinueTicking returns true

        BehaviorTreeTask, DecoratorTask, ParallelTask, SelectorLoopTask, etc.
        */
        public BranchTask GetParentBranch()
        {
            BehaviorTask pTopNode = this.m_parent;

            while (pTopNode != null)
            {
                BranchTask pBranch = pTopNode as BranchTask;
                if (pBranch != null && pBranch.isContinueTicking())
                {
                    return pBranch;
                }

                pTopNode = pTopNode.m_parent;
            }

            return null;
        }

        public EBTStatus GetStatus()
        {
            return this.m_status;
        }

        public BehaviorNode GetNode()
        {
            return this.m_node;
        }

        public BranchTask GetParent()
        {
            return this.m_parent;
        }

        bool getBooleanFromStatus(EBTStatus status)
        {
            if (status == EBTStatus.BT_FAILURE)
                return false;
            else if (status == EBTStatus.BT_SUCCESS)
                return true;
            else
            {
                Debug.LogError("Predicate can not return RUNNING status!");
                return false;
            }
        }

        public string GetClassNameString()
        {
            if (this.m_node != null)
            {
                return this.m_node.GetClassNameString();
            }

            string subBT = "SubBT";
            return subBT;
        }

        public int GetId()
        {
            return this.m_id;
        }

        public void SetId(int id)
        {
            this.m_id = id;
        }

        public static string GetTickInfo(Agent pAgent, BehaviorTask b, string action)
        {
#if !BEHAVIAC_RELEASE
            if (Config.IsLoggingOrSocketing)
            {
                if (pAgent != null && pAgent.IsMasked())
                {
                    //BEHAVIAC_PROFILE("GetTickInfo", true);

                    string bClassName = b.GetClassNameString();

                    //filter out intermediate bt, whose class name is empty
                    if (!string.IsNullOrEmpty(bClassName))
                    {
                        int nodeId = b.GetId();
                        BehaviorTreeTask bt = pAgent != null ? pAgent.btgetcurrent() : null;

                        //TestBehaviorGroup\scratch.xml.EventetTask[0]:enter
                        string bpstr = "";
                        if (bt != null)
                        {
                            string btName = bt.GetName();

                            bpstr = string.Format("{0}.xml->", btName);
                        }

                        bpstr += string.Format("{0}[{1}]", bClassName, nodeId);

                        if (!string.IsNullOrEmpty(action))
                        {
                            bpstr += string.Format(":{0}", action);
                        }

                        return bpstr;
                    }
                }
            }
#endif
            return string.Empty;
        }

        private static string GetActionResultStr(EActionResult actionResult)
        {
#if !BEHAVIAC_RELEASE
            if (Config.IsLoggingOrSocketing)
            {
                string actionResultStr = "";
                if (actionResult == EActionResult.EAR_success)
                {
                    actionResultStr = " [success]";
                }
                else if (actionResult == EActionResult.EAR_failure)
                {
                    actionResultStr = " [failure]";
                }
                else
                {
                    //although actionResult can be EAR_none or EAR_all, but, as this is the real result of an action
                    //it can only be success or failure
                    Debug.Check(false);
                }

                return actionResultStr;
            }
#endif
            return string.Empty;
        }


        ///return true if it is continuing running for the next tick
        /**
        this virtual function is used for those nodes which will run continuously in the next tick
        so that the tree can record it to tick it directly in the next tick.

        so usually, the leaf nodes except the condition nodes need to override it to return true.
        the branch nodes usually return false. however, parallel need to return true.
        */
        protected virtual bool isContinueTicking()
        {
            return false;
        }

        private void InstantiatePars(Agent pAgent)
        {
            BehaviorNode pNode = this.m_node;

            //pNode could be 0 when the bt is a sub tree of parallel node/referenced bt, etc.
            if (pNode != null && pNode.m_pars != null)
            {
                for (int i = 0; i < pNode.m_pars.Count; ++i)
                {
                    Property property_ = pNode.m_pars[i];

                    //					if(pAgent != null && property_.GetVariableName() == "par0_char_0")
                    //					{
                    //						behaviac.Debug.Check(true);
                    //					}

                    property_.Instantiate(pAgent);
                }
            }
        }

        private void UnInstantiatePars(Agent pAgent)
        {
            BehaviorNode pNode = this.m_node;

            //pNode could be 0 when the bt is a sub tree of parallel node/referenced bt, etc.
            if (pNode != null && pNode.m_pars != null)
            {
                for (int i = 0; i < pNode.m_pars.Count; ++i)
                {
                    Property property_ = pNode.m_pars[i];
                    property_.UnInstantiate(pAgent);
                }
            }
        }

        /**
        when a node is ended(success/failure), this dertermines if the exit status(success/failure) should be kept.
        if it needs to restart, then, the exit status is just returned but not kept
        */
        public virtual bool NeedRestart()
        {
            return false;
        }

        /**
        return false if the event handling  needs to be stopped
        return true, the event hanlding will be checked furtherly
        */
        public virtual bool onevent(Agent pAgent, string eventName)
        {
            if (this.m_status == EBTStatus.BT_RUNNING && this.m_node.HasEvents())
            {
                if (!this.CheckEvents(eventName, pAgent))
                {
                    return false;
                }
            }

            return true;
        }

        protected virtual bool onenter(Agent pAgent)
        {
            return true;
        }

        protected virtual void onexit(Agent pAgent, EBTStatus status)
        {
        }

        public bool onenter_action(Agent pAgent)
        {
            //this needs to be before onenter
            this.InstantiatePars(pAgent);

            bool bResult = this.onenter(pAgent);

            if (this.m_node != null)
            {
                if (!((BehaviorNode)(this.m_node)).enteraction_impl(pAgent))
                {
                    if (this.m_node.m_enterAction != null)
                    {
                        this.m_node.m_enterAction.Invoke(pAgent);
                    }
                }
            }

            if (!bResult)
            {
                this.UnInstantiatePars(pAgent);
            }
            else
            {
#if !BEHAVIAC_RELEASE
                //BEHAVIAC_PROFILE_DEBUGBLOCK("Debug", true);

                CHECK_BREAKPOINT(pAgent, this, "enter", bResult ? EActionResult.EAR_success : EActionResult.EAR_failure);
#endif
            }

            return bResult;
        }

        public void onexit_action(Agent pAgent, EBTStatus status)
        {
            this.onexit(pAgent, status);
            this.SetReturnStatus(EBTStatus.BT_INVALID);

            if (this.m_node != null)
            {
                bool exitImpl = ((BehaviorNode)(this.m_node)).exitaction_impl(pAgent);

                if (exitImpl || this.m_node.m_exitAction != null)
                {
                    if (!exitImpl && this.m_node.m_exitAction != null)
                    {
                        ms_lastExitStatus_ = status;
                        this.m_node.m_exitAction.Invoke(pAgent);
                
                        ms_lastExitStatus_ = EBTStatus.BT_INVALID;
                    }
                }
            }

            this.UnInstantiatePars(pAgent);
#if !BEHAVIAC_RELEASE
            if (Config.IsLoggingOrSocketing)
            {
                //BEHAVIAC_PROFILE_DEBUGBLOCK("Debug", true);
                if (status == EBTStatus.BT_SUCCESS)
                {
                    CHECK_BREAKPOINT(pAgent, this, "exit", EActionResult.EAR_success);
                }
                else
                {
                    CHECK_BREAKPOINT(pAgent, this, "exit", EActionResult.EAR_failure);
                }
            }
#endif
        }

        public void abort(Agent pAgent)
        {
            this.traverse(abort_handler_, pAgent, null);
        }

        ///reset the status to invalid
        public void reset(Agent pAgent)
        {
            //BEHAVIAC_PROFILE("BehaviorTask.reset");

            this.traverse(reset_handler_, pAgent, null);
        }

        static bool abort_handler(BehaviorTask node, Agent pAgent, object user_data)
        {
            if (node.m_status == EBTStatus.BT_RUNNING)
            {
                node.onexit_action(pAgent, EBTStatus.BT_FAILURE);

                node.m_status = EBTStatus.BT_FAILURE;

                node.SetCurrentTask(null);
            }

            return true;
        }

        static bool reset_handler(BehaviorTask node, Agent pAgent, object user_data)
        {
            node.m_status = EBTStatus.BT_INVALID;

            node.SetCurrentTask(null);
            //node->SetReturnStatus(BT_INVALID);
            //BEHAVIAC_ASSERT(node->GetReturnStatus() == BT_INVALID);

            //node.onreset(pAgent);

            return true;
        }

        public virtual void SetReturnStatus(EBTStatus status)
        {
        }

        public void SetStatus(EBTStatus s)
        {
            this.m_status = s;
        }

        public void SetParent(BranchTask parent)
        {
            this.m_parent = parent;
        }

        public virtual void SetCurrentTask(BehaviorTask node)
        {
        }

        public abstract void traverse(NodeHandler_t handler, Agent pAgent, object user_data);

        // isContinueTicking
        protected virtual EBTStatus update(Agent pAgent, EBTStatus childStatus)
        {
            return EBTStatus.BT_SUCCESS;
        }

    }

    // ============================================================================

    public class AttachmentTask : BehaviorTask
    {

        public override void traverse(NodeHandler_t handler, Agent pAgent, object user_data)
        {
            handler(this, pAgent, user_data);
        }
    }

    // ============================================================================

    public class LeafTask : BehaviorTask
    {

        public override void traverse(NodeHandler_t handler, Agent pAgent, object user_data)
        {
            handler(this, pAgent, user_data);
        }

    }

    // ============================================================================

    public abstract class BranchTask : BehaviorTask
    {
        //bookmark the current ticking node, it is different from m_activeChildIndex
        protected BehaviorTask m_currentTask;
        protected EBTStatus m_returnStatus;


        protected abstract void addChild(BehaviorTask pBehavior);

        public BehaviorTask GetCurrentTask()
        {
            return this.m_currentTask;
        }

        protected override bool isContinueTicking()
        {
            return false;
        }

        public override void SetCurrentTask(BehaviorTask node)
        {
            this.m_currentTask = node;
        }

        protected EBTStatus tickCurrentNode(Agent pAgent)
        {
            Debug.Check(this.m_currentTask != null);

            EBTStatus s = this.m_currentTask.GetStatus();

            if (s == EBTStatus.BT_INVALID || s == EBTStatus.BT_RUNNING)
            {
                //this.m_currentTask could be cleared in ::tick, to remember it
                BehaviorTask currentTask = this.m_currentTask;
                EBTStatus status = this.m_currentTask.exec(pAgent);

                //give the handling back to parents
                if (status != EBTStatus.BT_RUNNING)
                {
                    Debug.Check(currentTask.m_status == EBTStatus.BT_SUCCESS ||
                        currentTask.m_status == EBTStatus.BT_FAILURE ||
                        (currentTask.m_status == EBTStatus.BT_INVALID && currentTask.NeedRestart()));

                    BranchTask parentBranch = currentTask.GetParent();

                    this.SetCurrentTask(null);

                    //back track the parents until the branch
                    while (parentBranch != null && parentBranch != this)
                    {
                        status = parentBranch.update(pAgent, status);
                        if (status == EBTStatus.BT_RUNNING)
                        {
                            return EBTStatus.BT_RUNNING;
                        }

                        parentBranch.onexit_action(pAgent, status);

                        parentBranch.m_status = status;

                        Debug.Check(parentBranch.m_currentTask == null);

                        parentBranch = parentBranch.GetParent();
                    }
                }

                return status;
            }

            return s;
        }

        protected override EBTStatus update(Agent pAgent, EBTStatus childStatus)
        {
            EBTStatus status = EBTStatus.BT_INVALID;

            if (this.m_currentTask != null)
            {
                EBTStatus s = this.m_currentTask.GetStatus();
                if (s != EBTStatus.BT_RUNNING)
                {
                    this.SetCurrentTask(null);
                }
            }

            if (this.m_currentTask != null)
            {
                status = this.tickCurrentNode(pAgent);
            }

            return status;
        }

    }

    // ============================================================================

    public class CompositeTask : BranchTask
    {

        protected List<BehaviorTask> m_children = new List<BehaviorTask>();

        //book mark the current child
        protected int m_activeChildIndex = InvalidChildIndex;
        protected const int InvalidChildIndex = -1;


        protected CompositeTask()
        {
            m_activeChildIndex = InvalidChildIndex;
        }

        ~CompositeTask()
        {
            this.m_children.Clear();
        }

        public override void Init(BehaviorNode node)
        {
            base.Init(node);
            Debug.Check(node.GetChildrenCount() > 0);

            int childrenCount = node.GetChildrenCount();
            for (int i = 0; i < childrenCount; i++)
            {
                BehaviorNode childNode = node.GetChild(i);
                BehaviorTask childTask = childNode.CreateAndInitTask();

                this.addChild(childTask);
            }
        }

        protected override void addChild(BehaviorTask pBehavior)
        {
            pBehavior.SetParent(this);

            this.m_children.Add(pBehavior);
        }

        public override void traverse(NodeHandler_t handler, Agent pAgent, object user_data)
        {
            if (handler(this, pAgent, user_data))
            {
                for (int i = 0; i < this.m_children.Count; ++i)
                {
                    BehaviorTask task = this.m_children[i];
                    task.traverse(handler, pAgent, user_data);
                }
            }
        }

    }

    // ============================================================================

    public class SingeChildTask : BranchTask
    {

        protected BehaviorTask m_root;


        protected SingeChildTask()
        {
            m_root = null;
        }

        ~SingeChildTask()
        {
            m_root = null;
        }

        public override void Init(BehaviorNode node)
        {
            base.Init(node);

            Debug.Check(node.GetChildrenCount() <= 1);

            if (node.GetChildrenCount() == 1)
            {
                BehaviorNode childNode = node.GetChild(0);

                BehaviorTask childTask = childNode.CreateAndInitTask();

                this.addChild(childTask);
            }
            else
            {
                Debug.Check(true);
            }
        }

        protected override void addChild(BehaviorTask pBehavior)
        {
            pBehavior.SetParent(this);

            this.m_root = pBehavior;
        }

        public override void traverse(NodeHandler_t handler, Agent pAgent, object user_data)
        {
            if (handler(this, pAgent, user_data))
            {
                if (this.m_root != null)
                {
                    this.m_root.traverse(handler, pAgent, user_data);
                }
            }
        }

        protected override EBTStatus update(Agent pAgent, EBTStatus childStatus)
        {
            if (this.m_currentTask != null)
            {
                EBTStatus s = base.update(pAgent, childStatus);

                return s;
            }

            if (this.m_root != null)
            {
                EBTStatus s = this.m_root.exec(pAgent);
                return s;
            }

            return EBTStatus.BT_INVALID;
        }


    }

    // ============================================================================

    public abstract class DecoratorTask : SingeChildTask
    {
        protected DecoratorTask()
        {
            m_bDecorateWhenChildEnds = false;
        }

        ~DecoratorTask()
        {
        }

        public override void Init(BehaviorNode node)
        {
            base.Init(node);
            DecoratorNode pDN = node as DecoratorNode;

            this.m_bDecorateWhenChildEnds = pDN.m_bDecorateWhenChildEnds;
        }

        /*
        called when the child's tick returns success or failure.
        please note, it is not called if the child's tick returns running
        */
        protected abstract EBTStatus decorate(EBTStatus status);

        protected override bool isContinueTicking()
        {
            return true;
        }

        protected override EBTStatus update(Agent pAgent, EBTStatus childStatus)
        {
            EBTStatus status = base.update(pAgent, childStatus);

            if (!this.m_bDecorateWhenChildEnds || status != EBTStatus.BT_RUNNING)
            {
                EBTStatus result = this.decorate(status);

                if (status != EBTStatus.BT_RUNNING)
                {
                    BehaviorTask child = this.m_root;
                    if (child != null)
                    {
                        child.SetStatus(EBTStatus.BT_INVALID);
                    }

                    this.SetCurrentTask(null);
                }

                return result;
            }

            return EBTStatus.BT_RUNNING;
        }


        private bool m_bDecorateWhenChildEnds;
    }

    // ============================================================================

    public class BehaviorTreeTask : SingeChildTask
    {


        public override void Clear()
        {
            base.Clear();

            this.m_currentTask = null;
            this.m_returnStatus = EBTStatus.BT_INVALID;
            this.m_root = null;
        }

        public override bool NeedRestart()
        {
            BehaviorTask root = this.m_root;
            if (root != null && root.NeedRestart())
            {
                return true;
            }

            return false;
        }

        /**
        return the path relative to the workspace path
        */
        public string GetName()
        {
            Debug.Check(this.m_node is BehaviorTree);
            BehaviorTree bt = this.m_node as BehaviorTree;
            Debug.Check(bt != null);
            return bt.GetName();
        }


        protected override bool isContinueTicking()
        {
            return true;
        }

        protected override bool onenter(Agent pAgent)
        {
            return true;
        }

        protected override void onexit(Agent pAgent, EBTStatus s)
        {
        }


        public EBTStatus resume(Agent pAgent, EBTStatus status)
        {
            BranchTask prev = null;
            BehaviorTask p = this.m_currentTask;
            while (p is BranchTask)
            {
                BranchTask b = (BranchTask)p;
                if (b.GetCurrentTask() != null)
                {
                    p = b.GetCurrentTask();
                }
                else
                {
                    break;
                }
            }

            while (p != null)
            {
                BranchTask branch = p as BranchTask;
                if (branch != null)
                {
                    prev = branch;
                    p = branch.GetCurrentTask();
                }
                else
                {
                    prev = p.GetParent();
                    break;
                }
            }

            if (prev != null)
            {
                //prev is the last interrupted node, to find and let its parent update with 'status' which is the subtree's return status
                Debug.Check(status == EBTStatus.BT_SUCCESS || status == EBTStatus.BT_FAILURE);
                prev.onexit_action(pAgent, status);

                prev.SetReturnStatus(status);
            }

            return status;
        }

        protected override EBTStatus update(Agent pAgent, EBTStatus childStatus)
        {
            EBTStatus status = base.update(pAgent, childStatus);

            if (status != EBTStatus.BT_RUNNING)
            {
                this.SetCurrentTask(null);
            }

            return status;
        }

        /**
        return false if the event handling  needs to be stopped
        return true, the event hanlding will be checked furtherly
        */
        public override bool onevent(Agent pAgent, string eventName)
        {
            if (this.m_node.HasEvents())
            {
                bool bGoOn = this.m_root.onevent(pAgent, eventName);

                if (bGoOn)
                {
                    if (this.m_status == EBTStatus.BT_RUNNING && this.m_node.HasEvents())
                    {
                        if (!this.CheckEvents(eventName, pAgent))
                        {
                            return false;
                        }
                    }

                    return false;
                }
            }

            return true;
        }

    }

}
