/*
Copyright (c) Microsoft Corporation

All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in 
compliance with the License.  You may obtain a copy of the License 
at http://www.apache.org/licenses/LICENSE-2.0   


THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER 
EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF 
TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.  


See the Apache Version 2.0 License for specific language governing permissions and 
limitations under the License. 

*/

//
// � Microsoft Corporation.  All rights reserved.
//
#if REMOVE_FOR_YARN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Xml;
using Microsoft.Hpc.Scheduler;
using Microsoft.Hpc.Scheduler.Properties;
using Microsoft.Hpc.Dryad;
using Microsoft.Research.DryadLinq.Internal;
using System.Collections.Specialized;

namespace Microsoft.Research.DryadLinq
{
    internal class HpcJobSubmission : IHpcLinqJobSubmission
    {
        private HpcLinqContext m_context;
        private DryadJobSubmission m_job;
        private JobStatus m_status;

        internal void Initialize()
        {
            this.m_job.FriendlyName = m_context.Configuration.JobFriendlyName;

            //  if the user specified MinNodes and it is less than 2, return an error. Otherwise let job run with job template which 
            //      must specify a value of 2 or higher
            if (m_context.Configuration.JobMinNodes.HasValue && m_context.Configuration.JobMinNodes < 2)
            {
                throw new HpcLinqException(HpcLinqErrorCode.HpcLinqJobMinMustBe2OrMore,
                                           SR.HpcLinqJobMinMustBe2OrMore);
            }

            this.m_job.DryadJobMinNodes = m_context.Configuration.JobMinNodes;
            this.m_job.DryadJobMaxNodes = m_context.Configuration.JobMaxNodes;
            this.m_job.DryadNodeGroup = m_context.Configuration.NodeGroup;
            this.m_job.DryadUserName = m_context.Configuration.JobUsername;
            this.m_job.DryadPassword = m_context.Configuration.JobPassword;
            this.m_job.DryadRuntime = m_context.Configuration.JobRuntimeLimit;
            this.m_job.EnableSpeculativeDuplication = m_context.Configuration.EnableSpeculativeDuplication;
            this.m_job.RuntimeTraceLevel = (int)m_context.Configuration.RuntimeTraceLevel;
            this.m_job.GraphManagerNode = m_context.Configuration.GraphManagerNode;

            System.Collections.Specialized.NameValueCollection collection = new System.Collections.Specialized.NameValueCollection();
            
            foreach (var keyValuePair in m_context.Configuration.JobEnvironmentVariables)
            {
                collection.Add(keyValuePair.Key, keyValuePair.Value);
            }

            this.m_job.JobEnvironmentVariables = collection;
        }

        internal bool LocalJM
        {
            get
            {
                return m_job.Type == DryadJobSubmission.JobType.Local;
            }
            set
            {
                if (value == true)
                {
                    m_job.Type = DryadJobSubmission.JobType.Local;
                }
                else
                {
                    m_job.Type = DryadJobSubmission.JobType.Cluster;
                }
            }
        }

        internal string CommandLine
        {
            get
            {
                return m_job.CommandLine;
            }
            set
            {
                m_job.CommandLine = value;
            }
        }

        public string ErrorMsg
        {
            get
            {
                return m_job.ErrorMessage;
            }
            private set
            {
                m_job.ErrorMessage = value;
            }
        }

        internal HpcJobSubmission(HpcLinqContext context)
        {
            this.m_context = context;
            this.m_status = JobStatus.NotSubmitted;

            //@@TODO[P0] pass the runtime to the DryadJobSubmission so that it can use the scheduler instance.
            //@@TODO: Merge DryadJobSubmission into Ms.Hpc.Linq. Until then make sure Context is not disposed before DryadJobSubmission.
            this.m_job = new DryadJobSubmission(m_context.GetIScheduler());
        }

        public void AddJobOption(string fieldName, string fieldVal)
        {
            if (fieldName == "cmdline")
            {
                m_job.CommandLine = fieldVal;
            }
            else
            {
                throw new HpcLinqException(HpcLinqErrorCode.JobOptionNotImplemented,
                                           String.Format(SR.JobOptionNotImplemented, fieldName, fieldVal));
            }
        }

        public void AddLocalFile(string fileName)
        {
            m_job.AddFileToJob(fileName);
        }

        public void AddRemoteFile(string fileName)
        {
            string msg = String.Format("HpcJobSubmission.AddRemoteFile({0}) not implemented", fileName);
        }

        public JobStatus GetStatus()
        {
            if (this.m_status == JobStatus.Success ||
                this.m_status == JobStatus.Failure )
            {
                return this.m_status;
            }

            if (this.m_job == null)
            {
                return JobStatus.NotSubmitted;
            }

            switch (this.m_job.State)
            {
                case JobState.ExternalValidation:
                case JobState.Queued:
                case JobState.Submitted:
                case JobState.Validating:
                {
                    this.m_status = JobStatus.Waiting;
                    break;
                }
                case JobState.Configuring:
                case JobState.Running:
                case JobState.Canceling:
                case JobState.Finishing:
                {
                    this.m_status = JobStatus.Running;
                    break;
                }
                case JobState.Failed:
                    // a job only fails if the job manager fails.
                {
                    ISchedulerCollection tasks = this.m_job.Job.GetTaskList(null, null, false);
                    if (tasks.Count < 1)
                    {
                        this.ErrorMsg = this.m_job.ErrorMessage;
                        this.m_status = JobStatus.Failure;
                    }
                    else
                    {
                        ISchedulerTask jm = tasks[0] as ISchedulerTask;
                        switch (jm.State)
                        {
                            case TaskState.Finished:
                                this.m_status = JobStatus.Success;
                                break;
                            default:
                                this.m_status = JobStatus.Failure;
                                this.ErrorMsg = "JM error: " + jm.ErrorMessage;
                                break;
                        }
                    }
                    break;
                }
                case JobState.Canceled:
                {
                    this.ErrorMsg = this.m_job.ErrorMessage;
                    this.m_status = JobStatus.Failure;
                    break;
                }    
                case JobState.Finished:
                {
                    this.m_status = JobStatus.Success;
                    break;
                }
            }

            return this.m_status;
        }

        public void SubmitJob()
        {
            // Verify that the head node is set
            if (m_context.Configuration.HeadNode == null)
            {
                throw new HpcLinqException(HpcLinqErrorCode.ClusterNameMustBeSpecified,
                                           SR.ClusterNameMustBeSpecified);
            }
            
            try
            {
                this.m_job.SubmitJob();
            }
            catch (Exception e)
            {
                throw new HpcLinqException(HpcLinqErrorCode.SubmissionFailure,
                                           String.Format(SR.SubmissionFailure, m_context.Configuration.HeadNode), e);
            }
        }

        public JobStatus TerminateJob()
        {
            JobStatus status = GetStatus();
            switch (status)
            {
                case JobStatus.Failure:
                case JobStatus.NotSubmitted:
                case JobStatus.Success:
                case JobStatus.Cancelled:
                    // Nothing to do.
                    return status;
                default:
                    break;
            }

            this.m_job.CancelJob();
            return JobStatus.Cancelled;
        }

        public int GetJobId()
        {
            if (m_job == null || m_job.Job == null)
            {
                throw new InvalidOperationException("(internal) GetDryadJobInfo called when no job is available");
            }
            return m_job.Job.Id;
        }
    }
}
#else
namespace Microsoft.Research.DryadLinq
{
    internal class HpcJobSubmission : IHpcLinqJobSubmission
    {
        private HpcLinqContext m_context;

        public HpcJobSubmission(HpcLinqContext context)
        {
            m_context = context;
        }

        public void AddJobOption(string fieldName, string fieldVal)
        {
            throw new System.NotImplementedException();
        }

        public void AddLocalFile(string fileName)
        {
            throw new System.NotImplementedException();
        }

        public void AddRemoteFile(string fileName)
        {
            throw new System.NotImplementedException();
        }

        public string ErrorMsg
        {
            get { throw new System.NotImplementedException(); }
        }

        public JobStatus GetStatus()
        {
            throw new System.NotImplementedException();
        }

        public void SubmitJob()
        {
            throw new System.NotImplementedException();
        }

        public JobStatus TerminateJob()
        {
            throw new System.NotImplementedException();
        }

        public int GetJobId()
        {
            throw new System.NotImplementedException();
        }

        internal void Initialize()
        {
            throw new System.NotImplementedException();
        }
    }
}
#endif
