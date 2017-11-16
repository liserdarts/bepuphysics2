﻿using BepuUtilities.Memory;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepuPhysics.Collidables;
using BepuUtilities.Collections;
using System;

namespace BepuPhysics
{   
    //You could bitpack these two into 4 bytes, but the value of that is pretty darn questionable.
    public struct BodyConstraintReference
    {
        public int ConnectingConstraintHandle;
        public int BodyIndexInConstraint;
    }

    /// <summary>
    /// Stores a group of bodies- either the set of active bodies, or the bodies involved in an inactive simulation island.
    /// </summary>
    public struct BodySet
    {
        //Note that all body information is stored in AOS format.
        //While the pose integrator would technically benefit from (AO)SOA, it would only help in a magical infinite bandwidth scenario.
        //In practice, the pose integrator's actual AOSOA-benefitting chunk can't even scale to 2 threads, even with only 4-wide SIMD.
        //On top of that, the narrow phase and solver both need to access the body's information in a noncontiguous way. While the layout optimizer stages can help here to a degree,
        //the simple fact is that the scattered loads will likely waste a lot of cache line space- the majority, even, for wider SIMD bundles.
        //(Consider: noncontiguously sampling velocities.Linear.X on an AVX512 AOSOA layout would load a 64 byte cache line and use only 4 bytes of it!)

        //Plus, no one wants to deal with AOSOA layouts when writing game logic. Realistically, body data will be the most frequently accessed property in the engine, 
        //and not having to do a transpose to pull it into AOS is much less painful.

        /// <summary>
        /// Remaps a body index to its handle.
        /// </summary>
        public Buffer<int> IndexToHandle;

        public Buffer<RigidPose> Poses;
        public Buffer<BodyVelocity> Velocities;
        public Buffer<BodyInertia> LocalInertias;

        /// <summary>
        /// The collidables owned by each body in the set. Speculative margins, continuity settings, and shape indices can be changed directly.
        /// Shape indices cannot transition between pointing at a shape and pointing at nothing or vice versa without notifying the broad phase of the collidable addition or removal.
        /// </summary>
        public Buffer<Collidable> Collidables;
        /// <summary>
        /// Activity states of bodies in the set.
        /// </summary>
        public Buffer<BodyActivity> Activity;
        /// <summary>
        /// List of constraints associated with each body in the set.
        /// </summary>
        public Buffer<QuickList<BodyConstraintReference, Buffer<BodyConstraintReference>>> Constraints;

        public int Count;
        /// <summary>
        /// Gets whether this instance is backed by allocated memory.
        /// </summary>
        public bool Allocated { get { return IndexToHandle.Allocated; } }

        public BodySet(int initialCapacity, BufferPool pool) : this()
        {
            InternalResize(initialCapacity, pool);
        }

        internal int Add(ref BodyDescription bodyDescription, int handle)
        {
            var index = Count++;
            IndexToHandle[index] = handle;
            ref var collidable = ref Collidables[index];
            collidable.Shape = bodyDescription.Collidable.Shape;
            collidable.Continuity = bodyDescription.Collidable.Continuity;
            collidable.SpeculativeMargin = bodyDescription.Collidable.SpeculativeMargin;
            //Collidable's broad phase index is left unset. The simulation is responsible for attaching that data.

            Poses[index] = bodyDescription.Pose;
            Velocities[index] = bodyDescription.Velocity;
            LocalInertias[index] = bodyDescription.LocalInertia;
            return index;
        }

        internal bool RemoveAt(int bodyIndex, BufferPool pool, out int handle, out int movedBodyIndex, out int movedBodyHandle)
        {
            handle = IndexToHandle[bodyIndex];
            //Move the last body into the removed slot.
            --Count;
            bool bodyMoved = bodyIndex < Count;
            if (bodyMoved)
            {
                movedBodyIndex = Count;
                //Copy the memory state of the last element down.
                Poses[bodyIndex] = Poses[movedBodyIndex];
                Velocities[bodyIndex] = Velocities[movedBodyIndex];
                LocalInertias[bodyIndex] = LocalInertias[movedBodyIndex];
                Activity[bodyIndex] = Activity[movedBodyIndex];
                Collidables[bodyIndex] = Collidables[movedBodyIndex];
                ref var constraintsSlot = ref Constraints[bodyIndex];
                Debug.Assert(constraintsSlot.Count == 0, "Removing a body without first removing its constraints results in orphaned constraints that will break stuff. Don't do it!");
                constraintsSlot.Dispose(pool.SpecializeFor<BodyConstraintReference>());
                constraintsSlot = Constraints[movedBodyIndex];
                //Point the body handles at the new location.
                movedBodyHandle = IndexToHandle[movedBodyIndex];
                IndexToHandle[bodyIndex] = movedBodyHandle;
            }
            else
            {
                movedBodyIndex = -1;
                movedBodyHandle = -1;
            }
            //We rely on the collidable references being nonexistent beyond the body count.
            //TODO: is this still true? Are these inits required?
            Collidables[Count] = new Collidable();
            //The indices should also be set to all -1's beyond the body count.
            IndexToHandle[Count] = -1;
            return bodyMoved;
        }

        internal void ApplyDescriptionByIndex(int index, ref BodyDescription description)
        {
            BundleIndexing.GetBundleIndices(index, out var bundleIndex, out var innerIndex);
            Poses[index] = description.Pose;
            Velocities[index] = description.Velocity;
            LocalInertias[index] = description.LocalInertia;
            ref var collidable = ref Collidables[index];
            collidable.Continuity = description.Collidable.Continuity;
            collidable.SpeculativeMargin = description.Collidable.SpeculativeMargin;
            //Note that we change the shape here. If the collidable transitions from shapeless->shapeful or shapeful->shapeless, the broad phase has to be notified 
            //so that it can create/remove an entry. That's why this function isn't public.
            collidable.Shape = description.Collidable.Shape;
            ref var activity = ref Activity[index];
            activity.DeactivationThreshold = description.Activity.DeactivationThreshold;
            activity.MinimumTimestepsUnderThreshold = description.Activity.MinimumTimestepCountUnderThreshold;
        }

        public void GetDescription(int index, out BodyDescription description)
        {
            description.Pose = Poses[index];
            description.Velocity = Velocities[index];
            description.LocalInertia = LocalInertias[index];
            ref var collidable = ref Collidables[index];
            description.Collidable.Continuity = collidable.Continuity;
            description.Collidable.Shape = collidable.Shape;
            description.Collidable.SpeculativeMargin = collidable.SpeculativeMargin;
            ref var activity = ref Activity[index];
            description.Activity.DeactivationThreshold = activity.DeactivationThreshold;
            description.Activity.MinimumTimestepCountUnderThreshold = activity.MinimumTimestepsUnderThreshold;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddConstraint(int bodyIndex, int constraintHandle, int bodyIndexInConstraint, BufferPool pool)
        {
            BodyConstraintReference constraint;
            constraint.ConnectingConstraintHandle = constraintHandle;
            constraint.BodyIndexInConstraint = bodyIndexInConstraint;
            ref var constraints = ref Constraints[bodyIndex];
            if (constraints.Span.Length == constraints.Count)
                constraints.Resize(constraints.Span.Length * 2, pool.SpecializeFor<BodyConstraintReference>());
            constraints.AllocateUnsafely() = constraint;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveConstraint(int bodyIndex, int constraintHandle, int minimumConstraintCapacityPerBody, BufferPool pool)
        {
            //This uses a linear search. That's fine; bodies will rarely have more than a handful of constraints associated with them.
            //Attempting to use something like a hash set for fast removes would just introduce more constant overhead and slow it down on average.
            ref var list = ref Constraints[bodyIndex];
            for (int i = 0; i < list.Count; ++i)
            {
                ref var element = ref list[i];
                if (element.ConnectingConstraintHandle == constraintHandle)
                {
                    list.FastRemoveAt(i);
                    break;
                }
            }
            if (list.Count <= list.Span.Length / 2 && list.Span.Length >= minimumConstraintCapacityPerBody)
            {
                //The list has shrunk quite a bit, and it's above the maximum size. Might as well try to trim a little.
                var targetCapacity = list.Count > minimumConstraintCapacityPerBody ? list.Count : minimumConstraintCapacityPerBody;
                list.Resize(targetCapacity, pool.SpecializeFor<BodyConstraintReference>());
            }
        }

        struct ConstraintBodiesEnumerator<TInnerEnumerator> : IForEach<int> where TInnerEnumerator : IForEach<int>
        {
            public TInnerEnumerator InnerEnumerator;
            public int SourceBodyIndex;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void LoopBody(int connectedBodyIndex)
            {
                if (SourceBodyIndex != connectedBodyIndex)
                {
                    //Note that this may report the same body multiple times if it is connected multiple times! That's fine and potentially useful; let the user deal with it.
                    InnerEnumerator.LoopBody(connectedBodyIndex);
                }
            }

        }

        /// <summary>
        /// Enumerates all the bodies connected to a given body.
        /// Bodies which are connected by more than one constraint will be reported multiple times.
        /// </summary>
        /// <typeparam name="TEnumerator">Type of the enumerator to execute on each connected body.</typeparam>
        /// <param name="bodyIndex">Index of the body to enumerate the connections of. This body will not appear in the set of enumerated bodies, even if it is connected to itself somehow.</param>
        /// <param name="enumerator">Enumerator instance to run on each connected body.</param>
        /// <param name="solver">Solver from which to pull constraint body references.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnumerateConnectedBodies<TEnumerator>(int bodyIndex, ref TEnumerator enumerator, Solver solver) where TEnumerator : IForEach<int>
        {
            ref var list = ref Constraints[bodyIndex];
            ConstraintBodiesEnumerator<TEnumerator> constraintBodiesEnumerator;
            constraintBodiesEnumerator.InnerEnumerator = enumerator;
            constraintBodiesEnumerator.SourceBodyIndex = bodyIndex;

            //Note reverse iteration. This is useful when performing O(1) removals where the last element is put into the position of the removed element.
            //Non-reversed iteration would result in skipped elements if the loop body removed anything. This relies on convention; any remover should be aware of this order.
            for (int i = list.Count - 1; i >= 0; --i)
            {
                solver.EnumerateConnectedBodyIndices(list[i].ConnectingConstraintHandle, ref constraintBodiesEnumerator);
            }
            //Note that we have to assume the enumerator contains state mutated by the internal loop bodies.
            //If it's a value type, those mutations won't be reflected in the original reference. 
            //Copy them back in.
            enumerator = constraintBodiesEnumerator.InnerEnumerator;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Swap<T>(ref T a, ref T b)
        {
            var temp = a;
            a = b;
            b = temp;
        }

        /// <summary>
        /// Swaps the memory of two bodies. Indexed by memory slot, not by handle index.
        /// </summary>
        /// <param name="slotA">Memory slot of the first body to swap.</param>
        /// <param name="slotB">Memory slot of the second body to swap.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Swap(int slotA, int slotB, ref Buffer<BodyLocation> handleToIndex)
        {
            handleToIndex[IndexToHandle[slotA]].Index = slotB;
            handleToIndex[IndexToHandle[slotB]].Index = slotA;
            Swap(ref IndexToHandle[slotA], ref IndexToHandle[slotB]);
            Swap(ref Collidables[slotA], ref Collidables[slotB]);
            Swap(ref Poses[slotA], ref Poses[slotB]);
            Swap(ref Velocities[slotA], ref Velocities[slotB]);
            Swap(ref LocalInertias[slotA], ref LocalInertias[slotB]);
            Swap(ref Activity[slotA], ref Activity[slotB]);
            Swap(ref Constraints[slotA], ref Constraints[slotB]);
        }

        internal unsafe void InternalResize(int targetBodyCapacity, BufferPool pool)
        {
            Debug.Assert(targetBodyCapacity > 0, "Resize is not meant to be used as Dispose. If you want to return everything to the pool, use Dispose instead.");
            //Note that we base the bundle capacities on post-resize capacity of the IndexToHandle array. This simplifies the conditions on allocation, but increases memory use.
            //You may want to change this in the future if memory use is concerning.
            targetBodyCapacity = BufferPool<int>.GetLowestContainingElementCount(targetBodyCapacity);
            Debug.Assert(Poses.Length != BufferPool<RigidPoses>.GetLowestContainingElementCount(targetBodyCapacity), "Should not try to use internal resize of the result won't change the size.");
            pool.SpecializeFor<RigidPose>().Resize(ref Poses, targetBodyCapacity, Count);
            pool.SpecializeFor<BodyVelocity>().Resize(ref Velocities, targetBodyCapacity, Count);
            pool.SpecializeFor<BodyInertia>().Resize(ref LocalInertias, targetBodyCapacity, Count);
            pool.SpecializeFor<int>().Resize(ref IndexToHandle, targetBodyCapacity, Count);
            pool.SpecializeFor<Collidable>().Resize(ref Collidables, targetBodyCapacity, Count);
            pool.SpecializeFor<BodyActivity>().Resize(ref Activity, targetBodyCapacity, Count);
            pool.SpecializeFor<QuickList<BodyConstraintReference, Buffer<BodyConstraintReference>>>().Resize(ref Constraints, targetBodyCapacity, Count);
            //TODO: You should probably examine whether these protective initializations are still needed.
            //Initialize all the indices beyond the copied region to -1.
            Unsafe.InitBlockUnaligned(((int*)IndexToHandle.Memory) + Count, 0xFF, (uint)(sizeof(int) * (IndexToHandle.Length - Count)));
            //Collidables beyond the body count should all point to nothing, which corresponds to zero.
            Collidables.Clear(Count, Collidables.Length - Count);
        }

        public unsafe void Clear(BufferPool pool)
        {
            var constraintReferencePool = pool.SpecializeFor<BodyConstraintReference>();
            for (int i = 0; i < Count; ++i)
            {
                Constraints[i].Dispose(constraintReferencePool);
            }
            Count = 0;
            //TODO: Should confirm that these inits are still needed. They are for Handle->Location, but this is the opposite direction.
            Unsafe.InitBlockUnaligned(IndexToHandle.Memory, 0xFF, (uint)(sizeof(int) * IndexToHandle.Length));
        }

        public void EnsureConstraintListCapacities(int minimumConstraintCapacityPerBody, BufferPool pool)
        {
            var constraintPool = pool.SpecializeFor<BodyConstraintReference>();
            for (int i = 0; i < Count; ++i)
            {
                Constraints[i].EnsureCapacity(minimumConstraintCapacityPerBody, constraintPool);
            }
        }

        public void ResizeConstraintListCapacities(int targetConstraintCapacityPerBody, BufferPool pool)
        {
            var constraintPool = pool.SpecializeFor<BodyConstraintReference>();
            for (int i = 0; i < Count; ++i)
            {
                ref var list = ref Constraints[i];
                var targetCapacityForBody = BufferPool<BodyConstraintReference>.GetLowestContainingElementCount(Math.Max(list.Count, targetConstraintCapacityPerBody));
                if (targetCapacityForBody != list.Span.Length)
                    list.Resize(targetCapacityForBody, constraintPool);
            }
        }

        /// <summary>
        /// Disposes the buffers, but nothing inside of the buffers. Per-body constraint lists stored in the set will not be returned.
        /// </summary>
        /// <param name="pool">Pool to return the set's top level buffers to.</param>
        public void DisposeBuffers(BufferPool pool)
        {
            pool.SpecializeFor<RigidPose>().Return(ref Poses);
            pool.SpecializeFor<BodyVelocity>().Return(ref Velocities);
            pool.SpecializeFor<BodyInertia>().Return(ref LocalInertias);
            pool.SpecializeFor<int>().Return(ref IndexToHandle);
            pool.SpecializeFor<Collidable>().Return(ref Collidables);
            pool.SpecializeFor<BodyActivity>().Return(ref Activity);
            pool.SpecializeFor<QuickList<BodyConstraintReference, Buffer<BodyConstraintReference>>>().Return(ref Constraints);
        }

        /// <summary>
        /// Disposes the body set's buffers and any resources within them.
        /// </summary>
        /// <param name="pool">Pool to return resources to.</param>
        public void Dispose(BufferPool pool)
        {
            var constraintReferencePool = pool.SpecializeFor<BodyConstraintReference>();
            for (int i = 0; i < Count; ++i)
            {
                Constraints[i].Dispose(constraintReferencePool);
            }
            DisposeBuffers(pool);

        }
    }
}