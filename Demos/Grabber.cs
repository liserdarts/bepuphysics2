﻿using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using DemoRenderer;
using DemoRenderer.Constraints;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Quaternion = BepuUtilities.Quaternion;

namespace Demos
{
    struct Grabber
    {
        bool active;
        BodyReference body;
        float t;
        Vector3 localGrabPoint;
        int linearMotorHandle;
        int angularMotorHandle;

        struct RayHitHandler : IRayHitHandler
        {
            public float T;
            public CollidableReference HitCollidable;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool AllowTest(CollidableReference collidable)
            {
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable)
            {
                //We are only interested in the earliest hit. This callback is executing within the traversal, so modifying maximumT informs the traversal
                //that it can skip any AABBs which are more distant than the new maximumT.
                if (t < maximumT)
                    maximumT = t;
                if (t < T)
                {
                    //Cache the earliest impact.
                    T = t;
                    HitCollidable = collidable;
                }
            }
        }

        Vector3 GetRayDirection(Camera camera, bool mouseLocked, in Vector2 normalizedMousePosition)
        {
            //The ray direction depends on the camera and whether the camera is locked.
            if (mouseLocked)
            {
                return camera.Forward;
            }
            var unitPlaneHalfHeight = MathF.Tan(camera.FieldOfView * 0.5f);
            var unitPlaneHalfWidth = unitPlaneHalfHeight * camera.AspectRatio;
            var localRayDirection = new Vector3(
                new Vector2(unitPlaneHalfWidth, unitPlaneHalfHeight) * 2 * new Vector2(normalizedMousePosition.X - 0.5f, 0.5f - normalizedMousePosition.Y), -1);
            Quaternion.TransformWithoutOverlap(localRayDirection, camera.OrientationQuaternion, out var rayDirection);
            return rayDirection;
        }

        void CreateMotorDescription(in Vector3 target, float inverseMass, out OneBodyLinearServo linearDescription, out OneBodyAngularMotor angularDescription)
        {
            linearDescription = new OneBodyLinearServo
            {
                LocalOffset = localGrabPoint,
                Target = target,
                ServoSettings = new ServoSettings
                {
                    MaximumSpeed = float.MaxValue,
                    MaximumForce = 240f / inverseMass
                },
                SpringSettings = new SpringSettings(10, 2),
            };
            angularDescription = new OneBodyAngularMotor
            {
                TargetVelocity = default,
                Settings = new MotorSettings(240f / inverseMass, 1e-5f)
            };
        }

        public void Update(Simulation simulation, Camera camera, bool mouseLocked, bool shouldGrab, in Vector2 normalizedMousePosition)
        {
            var bodyExists = body.Exists;
            if (active && (!shouldGrab || !bodyExists))
            {
                active = false;
                if (bodyExists)
                {
                    //If the body wasn't removed, then the constraint should be removed.
                    //(Body removal forces connected constraints to removed, so in that case we wouldn't have to worry about it.)
                    simulation.Solver.Remove(linearMotorHandle);
                    if (!Bodies.HasLockedInertia(body.LocalInertia.InverseInertiaTensor))
                        simulation.Solver.Remove(angularMotorHandle);
                }
                body = new BodyReference();
            }
            else if (shouldGrab && !active)
            {
                var rayDirection = GetRayDirection(camera, mouseLocked, normalizedMousePosition);
                var hitHandler = default(RayHitHandler);
                hitHandler.T = float.MaxValue;
                simulation.RayCast(camera.Position, rayDirection, float.MaxValue, ref hitHandler);
                if (hitHandler.T < float.MaxValue && hitHandler.HitCollidable.Mobility == CollidableMobility.Dynamic)
                {
                    //Found something to grab!
                    t = hitHandler.T;
                    body = new BodyReference(hitHandler.HitCollidable.Handle, simulation.Bodies);
                    var hitLocation = camera.Position + rayDirection * t;
                    RigidPose.TransformByInverse(hitLocation, body.Pose, out localGrabPoint);
                    active = true;
                    CreateMotorDescription(hitLocation, body.LocalInertia.InverseMass, out var linearDescription, out var angularDescription);
                    linearMotorHandle = simulation.Solver.Add(body.Handle, ref linearDescription);
                    if (!Bodies.HasLockedInertia(body.LocalInertia.InverseInertiaTensor))
                        angularMotorHandle = simulation.Solver.Add(body.Handle, ref angularDescription);
                }
            }
            else if (active)
            {
                Quaternion.TransformWithoutOverlap(localGrabPoint, body.Pose.Orientation, out var grabPointOffset);
                var grabbedPoint = grabPointOffset + body.Pose.Position;

                var rayDirection = GetRayDirection(camera, mouseLocked, normalizedMousePosition);
                var targetPoint = camera.Position + rayDirection * t;

                CreateMotorDescription(targetPoint, body.LocalInertia.InverseMass, out var linearDescription, out var angularDescription);
                simulation.Solver.ApplyDescription(linearMotorHandle, ref linearDescription);
                if (!Bodies.HasLockedInertia(body.LocalInertia.InverseInertiaTensor))
                    simulation.Solver.ApplyDescription(angularMotorHandle, ref angularDescription);
                body.Activity.TimestepsUnderThresholdCount = 0;
            }
        }

        public void Draw(LineExtractor lines, Camera camera, bool mouseLocked, bool shouldGrab, in Vector2 normalizedMousePosition)
        {
            if (shouldGrab && !active && mouseLocked)
            {
                //Draw a crosshair if there is no mouse cursor.
                var center = camera.Position + camera.Forward * (camera.NearClip * 10);
                var crosshairLength = 0.1f * camera.NearClip * MathF.Tan(camera.FieldOfView * 0.5f);
                var rightOffset = camera.Right * crosshairLength;
                var upOffset = camera.Up * crosshairLength;
                lines.Allocate() = new LineInstance(center - rightOffset, center + rightOffset, new Vector3(1, 0, 0), new Vector3());
                lines.Allocate() = new LineInstance(center - upOffset, center + upOffset, new Vector3(1, 0, 0), new Vector3());
            }
        }
    }


}
