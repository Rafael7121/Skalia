using System;
using System.Collections.Generic;

namespace SkaliaDeps
{
    internal enum BotBehaviorStatus
    {
        Success,
        Failure,
        Running
    }

    internal interface IBotBehaviorNode
    {
        BotBehaviorStatus Tick();
    }

    internal sealed class BotConditionNode : IBotBehaviorNode
    {
        private readonly Func<bool> m_Condition;

        public BotConditionNode(Func<bool> condition)
        {
            m_Condition = condition;
        }

        public BotBehaviorStatus Tick()
        {
            return m_Condition()
                ? BotBehaviorStatus.Success
                : BotBehaviorStatus.Failure;
        }
    }

    internal sealed class BotActionNode : IBotBehaviorNode
    {
        private readonly Func<BotBehaviorStatus> m_Action;

        public BotActionNode(Func<BotBehaviorStatus> action)
        {
            m_Action = action;
        }

        public BotBehaviorStatus Tick()
        {
            return m_Action();
        }
    }

    /// <summary>
    /// Re-evaluates children from highest priority every tick. Running children
    /// therefore remain interruptible by any condition above them.
    /// </summary>
    internal sealed class BotReactiveSelectorNode : IBotBehaviorNode
    {
        private readonly IReadOnlyList<IBotBehaviorNode> m_Children;

        public BotReactiveSelectorNode(params IBotBehaviorNode[] children)
        {
            m_Children = children;
        }

        public BotBehaviorStatus Tick()
        {
            foreach (var child in m_Children)
            {
                var status = child.Tick();
                if (status != BotBehaviorStatus.Failure)
                {
                    return status;
                }
            }
            return BotBehaviorStatus.Failure;
        }
    }

    internal sealed class BotReactiveSequenceNode : IBotBehaviorNode
    {
        private readonly IReadOnlyList<IBotBehaviorNode> m_Children;

        public BotReactiveSequenceNode(params IBotBehaviorNode[] children)
        {
            m_Children = children;
        }

        public BotBehaviorStatus Tick()
        {
            foreach (var child in m_Children)
            {
                var status = child.Tick();
                if (status != BotBehaviorStatus.Success)
                {
                    return status;
                }
            }
            return BotBehaviorStatus.Success;
        }
    }

    internal sealed class BotUtilityCandidate
    {
        private readonly Func<float> m_Score;

        public BotUtilityCandidate(Func<float> score, IBotBehaviorNode node, bool isThreatResponse = false)
        {
            m_Score = score;
            Node = node;
            IsThreatResponse = isThreatResponse;
        }

        public IBotBehaviorNode Node { get; }

        // A reaction to a threat (flee, swarm, burner): it may interrupt
        // routine (loot, combat, exploration) at any time, commitment or not.
        public bool IsThreatResponse { get; }
        public float CurrentScore { get; private set; }

        public void Evaluate()
        {
            CurrentScore = m_Score();
        }
    }

    /// <summary>
    /// Re-evaluates contextual utility every tick. If the best candidate cannot
    /// act, the next candidate is attempted without inventing combination states.
    /// </summary>
    internal sealed class BotUtilitySelectorNode : IBotBehaviorNode
    {
        private readonly BotUtilityCandidate[] m_Candidates;
        private Func<float>? m_Clock;
        private Func<bool>? m_IsEmergency;
        private float m_CommitSeconds;
        private float m_EmergencyScore = float.PositiveInfinity;
        private BotUtilityCandidate? m_Active;
        private float m_ActiveSince;

        public BotUtilitySelectorNode(params BotUtilityCandidate[] candidates)
        {
            m_Candidates = candidates;
        }

        // Every tick started from zero: a candidate whose gate opened for a
        // moment took over and the one it replaced came back a second later,
        // about 500 objective changes per hour per bot (0.1.162 log). A
        // candidate that is still allowed now keeps running for a minimum
        // time before another one takes its place, unless the newcomer is an
        // emergency by score (swarm, low-health flee) or the bot is in one.
        public BotUtilitySelectorNode WithCommitment(
            Func<float> clock,
            Func<bool> isEmergency,
            float commitSeconds,
            float emergencyScore)
        {
            m_Clock = clock;
            m_IsEmergency = isEmergency;
            m_CommitSeconds = commitSeconds;
            m_EmergencyScore = emergencyScore;
            return this;
        }

        public BotBehaviorStatus Tick()
        {
            foreach (var candidate in m_Candidates)
            {
                candidate.Evaluate();
            }

            Array.Sort(
                m_Candidates,
                (left, right) => right.CurrentScore.CompareTo(left.CurrentScore));
            var now = m_Clock != null ? m_Clock() : 0f;
            if (m_Clock != null
                && m_Active != null
                && m_Active.CurrentScore >= 0f
                && m_Candidates[0] != m_Active
                && m_Candidates[0].CurrentScore < m_EmergencyScore
                && !(m_Candidates[0].IsThreatResponse && !m_Active.IsThreatResponse)
                && now - m_ActiveSince < m_CommitSeconds
                && !(m_IsEmergency?.Invoke() ?? false))
            {
                var kept = m_Active.Node.Tick();
                if (kept != BotBehaviorStatus.Failure)
                {
                    return kept;
                }
            }
            foreach (var candidate in m_Candidates)
            {
                if (candidate.CurrentScore < 0f)
                {
                    break;
                }
                var status = candidate.Node.Tick();
                if (status != BotBehaviorStatus.Failure)
                {
                    if (candidate != m_Active)
                    {
                        m_Active = candidate;
                        m_ActiveSince = now;
                    }
                    return status;
                }
            }
            m_Active = null;
            return BotBehaviorStatus.Failure;
        }
    }
}
