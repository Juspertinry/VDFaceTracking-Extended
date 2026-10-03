using Elements.Core;
using FrooxEngine;
using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using VirtualDesktop.FaceTracking;

namespace VDFaceTracking
{
    public class VDProxy : IInputDriver
    {
        private const string BodyStateMapName = "VirtualDesktop.BodyState";
        private const string BodyStateEventName = "VirtualDesktop.BodyStateEvent";
        private MemoryMappedFile _mappedFile;
        private MemoryMappedViewAccessor _mappedView;
        private unsafe FaceState* _faceState;
        private EventWaitHandle _faceStateEvent;

        private CancellationTokenSource cancellationTokenSource;

        private bool? _isTracking;

        private const byte FaceValidFlag = 0b0001;
        private const byte TongueShapesFlag = 0b0010;
        private volatile bool _extendedTongue;

        private enum TongueSource { Stock, Flagged, Detected }
        private TongueSource? _tongueSource;

        private const float BoltOnMinExtension = 0.3f;
        private const float BoltOnTolerance = 0.01f;
        private const int BoltOnMinFrames = 30;
        private const long BoltOnMinMilliseconds = 1000;
        private int _boltOnFrames;
        private long _boltOnSince;
        private bool _boltOnDetected;

        private Thread thread;

        private const int NATURAL_EXPRESSIONS_COUNT = FBExpression.Max;
        private float[] expressions = new float[NATURAL_EXPRESSIONS_COUNT + (8 * 2)];

        #region RESONITE VARIABLES
        private InputInterface _input;
        public int UpdateOrder => 100;
        private Mouth mouth;
        private Eyes eyes;
        #endregion

        private bool? IsTracking
        {
            get => this._isTracking;
            set
            {
                bool? nullable = value;
                bool? isTracking = this._isTracking;
                if (nullable.GetValueOrDefault() == isTracking.GetValueOrDefault() & nullable.HasValue == isTracking.HasValue)
                    return;
                this._isTracking = value;
                if (value.Value)
                    VDFaceTracking.Msg("Tracking is now active!");
                else
                    VDFaceTracking.Msg("Tracking is not active. Make sure you are connected to your computer, a VR game or SteamVR is launched and 'Forward tracking data' is enabled in the Streaming tab.");
            }
        }

        public virtual void UpdateThread()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    Update();
                }
                catch
                {
                }
            }
        }

        public virtual unsafe void Update()
        {
            if (this._faceStateEvent.WaitOne(50))
            {
                this.UpdateTracking();
            }
            else
            {
                FaceState* faceState = this._faceState;
                this.IsTracking = new bool?((IntPtr)faceState != IntPtr.Zero && (faceState->LeftEyeIsValid || faceState->RightEyeIsValid || faceState->IsEyeFollowingBlendshapesValid || (faceState->FaceFlags & FaceValidFlag) != 0));
            }
        }

        private unsafe void UpdateTracking()
        {
            bool flag = false;
            FaceState* faceState = this._faceState;
            if ((IntPtr)faceState != IntPtr.Zero)
            {
                float* expressionWeights = faceState->ExpressionWeights;

                if (faceState->LeftEyeIsValid)
                {
                    Pose leftEyePose = faceState->LeftEyePose;

                    expressions[FBExpression.LeftRot_x] = leftEyePose.Orientation.X;
                    expressions[FBExpression.LeftRot_y] = leftEyePose.Orientation.Y;
                    expressions[FBExpression.LeftRot_z] = leftEyePose.Orientation.Z;
                    expressions[FBExpression.LeftRot_w] = leftEyePose.Orientation.W;
                    
                    expressions[FBExpression.LeftPos_x] = leftEyePose.Position.X;
                    expressions[FBExpression.LeftPos_y] = leftEyePose.Position.Y;
                    expressions[FBExpression.LeftPos_z] = leftEyePose.Position.Z;

                    flag = true;
                }

                if(faceState->RightEyeIsValid)
                {
                    Pose rightEyePose = faceState->RightEyePose;

                    expressions[FBExpression.RightRot_x] = rightEyePose.Orientation.X;
                    expressions[FBExpression.RightRot_y] = rightEyePose.Orientation.Y;
                    expressions[FBExpression.RightRot_z] = rightEyePose.Orientation.Z;
                    expressions[FBExpression.RightRot_w] = rightEyePose.Orientation.W;

                    expressions[FBExpression.RightPos_x] = rightEyePose.Position.X;
                    expressions[FBExpression.RightPos_y] = rightEyePose.Position.Y;
                    expressions[FBExpression.RightPos_z] = rightEyePose.Position.Z;

                    flag = true;
                }

                if ((faceState->FaceFlags & FaceValidFlag) != 0 && faceState->IsEyeFollowingBlendshapesValid)
                {
                    for(int i = 0; i < NATURAL_EXPRESSIONS_COUNT; ++i)
                        expressions[i] = expressionWeights[i];

                    UpdateTongueSource(faceState->FaceFlags);

                    flag = true;
                }
            }
            this.IsTracking = new bool?(flag);
        }

        private void UpdateTongueSource(byte faceFlags)
        {
            TongueSource source;
            if ((faceFlags & TongueShapesFlag) != 0)
            {
                source = TongueSource.Flagged;
            }
            else
            {
                UpdateBoltOnDetection();
                source = _boltOnDetected ? TongueSource.Detected : TongueSource.Stock;
            }

            if (source == _tongueSource)
                return;

            _tongueSource = source;
            _extendedTongue = source != TongueSource.Stock;

            switch (source)
            {
                case TongueSource.Flagged:
                    VDFaceTracking.Msg("Virtual Desktop is sending extended tongue shapes, using them for tongue tracking.");
                    break;
                case TongueSource.Detected:
                    VDFaceTracking.Msg("BoltOn tongue data detected without the extended tongue flag, using it for tongue tracking.");
                    break;
                default:
                    VDFaceTracking.Msg("Using stock tongue tracking.");
                    break;
            }
        }

        private void UpdateBoltOnDetection()
        {
            float extension = expressions[FBExpression.TongueExtOut];
            float left = expressions[FBExpression.TongueExtLeft];
            float right = expressions[FBExpression.TongueExtRight];
            float up = expressions[FBExpression.TongueExtUp];
            float down = expressions[FBExpression.TongueExtDown];
            float stockOut = expressions[FBExpression.TongueOut];

            bool consistent = InBoltOnRange(extension, 1f)
                && InBoltOnRange(left, extension) && InBoltOnRange(right, extension)
                && InBoltOnRange(up, extension) && InBoltOnRange(down, extension)
                && !(left > 0f && right > 0f)
                && !(up > 0f && down > 0f)
                && stockOut >= 0f && stockOut <= BoltOnTolerance;

            if (!consistent)
            {
                _boltOnFrames = 0;
                _boltOnDetected = false;
                return;
            }

            if (_boltOnDetected || stockOut != 0f || extension < BoltOnMinExtension)
                return;

            long now = Environment.TickCount64;
            if (_boltOnFrames++ == 0)
                _boltOnSince = now;

            if (_boltOnFrames >= BoltOnMinFrames && now - _boltOnSince >= BoltOnMinMilliseconds)
                _boltOnDetected = true;
        }

        private static bool InBoltOnRange(float value, float max) => value >= 0f && value <= max + BoltOnTolerance;

        internal unsafe bool Initialize()
        {
            // MemoryMappedFile is supported only on Windows, therefore we should handle it gracefully.
            if (!OperatingSystem.IsWindows())
            {
                VDFaceTracking.Warn("This mod can not run on non-windows OS. Virtual Desktop proxy initialization is going to be skipped.");
                return false;
            }

            try
            {
                int size = Marshal.SizeOf<FaceState>();
                this._mappedFile = MemoryMappedFile.OpenExisting(BodyStateMapName, MemoryMappedFileRights.ReadWrite);
                this._mappedView = this._mappedFile.CreateViewAccessor(0L, (long)size);

                byte* numPtr = null;
                _mappedView.SafeMemoryMappedViewHandle.AcquirePointer(ref numPtr);
                this._faceState = (FaceState*) numPtr;
                this._faceStateEvent = EventWaitHandle.OpenExisting(BodyStateEventName);
                VDFaceTracking.Msg("Opened MemoryMappedFile. Everything should be working!");

                cancellationTokenSource = new CancellationTokenSource();
                thread = new Thread(UpdateThread);
                thread.Start();

                return true;
            }
            catch
            {
                VDFaceTracking.Error("Failed to open MemoryMappedFile. Make sure the Virtual Desktop Streamer (v1.30 or later) is running.");
                return false;
            }
        }

        internal unsafe void Teardown()
        {
            cancellationTokenSource.Cancel();

            if (thread != null)
                thread.Interrupt();

            cancellationTokenSource.Dispose();

            if ((IntPtr)_faceState != IntPtr.Zero)
            {
                _faceState = (FaceState*)null;
                if (_mappedView != null)
                {
                    _mappedView.Dispose();
                    _mappedView = null;
                }
                if (_mappedFile != null)
                {
                    _mappedFile.Dispose();
                    _mappedFile = null;
                }
            }
            if (_faceStateEvent != null)
            {
                _faceStateEvent.Dispose();
                _faceStateEvent = null;
            }
            _isTracking = new bool?();
        }

        bool IsValid(float3 value) => IsValid(value.x) && IsValid(value.y) && IsValid(value.z);
        bool IsValid(floatQ value) => IsValid(value.x) && IsValid(value.y) && IsValid(value.z) && IsValid(value.w) && InRange(value.x, new float2(1, -1)) && InRange(value.y, new float2(1, -1)) && InRange(value.z, new float2(1, -1)) && InRange(value.w, new float2(1, -1)) && (value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w) > 0.0001f;

        bool IsValid(float value) => !float.IsInfinity(value) && !float.IsNaN(value);

        bool InRange(float value, float2 range) => (value <= range.x && value >= range.y);

        public struct EyeGazeData
        {
            public bool isValid;
            public float3 position;
            public floatQ rotation;
            public float open;
            public float squeeze;
            public float wide;
            public float gazeConfidence;
        }

        public EyeGazeData GetEyeData(FBEye fbEye)
        {
            EyeGazeData eyeRet = new EyeGazeData();
            switch (fbEye)
            {
                case FBEye.Left:
                    eyeRet.position = new float3(expressions[FBExpression.LeftPos_x], -expressions[FBExpression.LeftPos_y], expressions[FBExpression.LeftPos_z]);
                    eyeRet.rotation = new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_y], -expressions[FBExpression.LeftRot_z], expressions[FBExpression.LeftRot_w]);
                    eyeRet.open = MathX.Max(0, expressions[FBExpression.Eyes_Closed_L]);
                    eyeRet.squeeze = expressions[FBExpression.Lid_Tightener_L];
                    eyeRet.wide = expressions[FBExpression.Upper_Lid_Raiser_L];
                    eyeRet.isValid = IsValid(eyeRet.position);
                    return eyeRet;
                case FBEye.Right:
                    eyeRet.position = new float3(expressions[FBExpression.RightPos_x], -expressions[FBExpression.RightPos_y], expressions[FBExpression.RightPos_z]);
                    eyeRet.rotation = new floatQ(-expressions[FBExpression.RightRot_x], -expressions[FBExpression.RightRot_y], -expressions[FBExpression.RightRot_z], expressions[FBExpression.RightRot_w]);
                    eyeRet.open = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R]);
                    eyeRet.squeeze = expressions[FBExpression.Lid_Tightener_R];
                    eyeRet.wide = expressions[FBExpression.Upper_Lid_Raiser_R];
                    eyeRet.isValid = IsValid(eyeRet.position);
                    return eyeRet;
                default:
                    throw new Exception($"Invalid eye argument: {fbEye}");
            }
        }

        public void CollectDeviceInfos(DataTreeList list)
        {
            var eyeDataTreeDictionary = new DataTreeDictionary();
            eyeDataTreeDictionary.Add("Name", "Quest Pro Eye Tracking");
            eyeDataTreeDictionary.Add("Type", "Eye Tracking");
            eyeDataTreeDictionary.Add("Model", "Quest Pro");
            list.Add(eyeDataTreeDictionary);

            var mouthDataTreeDictionary = new DataTreeDictionary();
            mouthDataTreeDictionary.Add("Name", "Quest Pro Face Tracking");
            mouthDataTreeDictionary.Add("Type", "Lip Tracking");
            mouthDataTreeDictionary.Add("Model", "Quest Pro");
            list.Add(mouthDataTreeDictionary);
        }

        public void RegisterInputs(InputInterface inputInterface)
        {
            _input = inputInterface;
            eyes = new Eyes(_input, "Quest Pro Eye Tracking", false);
            mouth = new Mouth(_input, "Quest Pro Face Tracking", new MouthParameterGroup[] {
                MouthParameterGroup.JawOpen,
                MouthParameterGroup.JawPose,
                MouthParameterGroup.TonguePose,
                MouthParameterGroup.LipRaise,
                MouthParameterGroup.LipHorizontal,
                MouthParameterGroup.SmileFrown,
                MouthParameterGroup.MouthDimple,
                MouthParameterGroup.MouthPout,
                MouthParameterGroup.LipOverturn,
                MouthParameterGroup.LipOverUnder,
                MouthParameterGroup.LipStretchTighten,
                MouthParameterGroup.LipsPress,
                MouthParameterGroup.CheekPuffSuck,
                MouthParameterGroup.CheekRaise,
                MouthParameterGroup.ChinRaise,
                MouthParameterGroup.NoseWrinkle
            });
        }

        public void UpdateInputs(float deltaTime)
        {
            UpdateMouth(deltaTime);
            UpdateEyes(deltaTime);
        }

        void UpdateEye(Eye eye, EyeGazeData data)
        {
            bool _isValid = IsValid(data.open);
            _isValid &= IsValid(data.position);
            _isValid &= IsValid(data.wide);
            _isValid &= IsValid(data.squeeze);
            _isValid &= IsValid(data.rotation);
            _isValid &= eye.IsTracking;

            eye.IsTracking = _isValid;

            if (eye.IsTracking)
            {
                eye.UpdateWithRotation(MathX.Slerp(floatQ.Identity, data.rotation, VDFaceTracking.EyeMoveMult));
                eye.Openness = MathX.Pow(MathX.FilterInvalid(data.open, 0.0f), VDFaceTracking.EyeOpenExponent);
                eye.Widen = data.wide * VDFaceTracking.EyeWideMult;
            }
        }

        void UpdateEyes(float deltaTime)
        {
            eyes.IsEyeTrackingActive = _input.VR_Active;

            eyes.LeftEye.IsTracking = _input.VR_Active;

            var leftEyeData = VDFaceTracking.proxy.GetEyeData(FBEye.Left);
            var rightEyeData = VDFaceTracking.proxy.GetEyeData(FBEye.Right);

            eyes.LeftEye.IsTracking = leftEyeData.isValid;
            eyes.LeftEye.RawPosition = leftEyeData.position;
            eyes.LeftEye.PupilDiameter = 0.004f;
            eyes.LeftEye.Squeeze = leftEyeData.squeeze;
            eyes.LeftEye.Frown = expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L] * VDFaceTracking.EyeExpressionMult;
            eyes.LeftEye.InnerBrowVertical = expressions[FBExpression.Inner_Brow_Raiser_L] + -expressions[FBExpression.Brow_Lowerer_L]; // Seems to fix eyebrows lowering
            eyes.LeftEye.OuterBrowVertical = expressions[FBExpression.Outer_Brow_Raiser_L] + -expressions[FBExpression.Brow_Lowerer_L];
            eyes.LeftEye.Squeeze = expressions[FBExpression.Brow_Lowerer_L];

            UpdateEye(eyes.LeftEye, leftEyeData);

            eyes.RightEye.IsTracking = rightEyeData.isValid;
            eyes.RightEye.RawPosition = rightEyeData.position;
            eyes.RightEye.PupilDiameter = 0.004f;
            eyes.RightEye.Squeeze = rightEyeData.squeeze;
            eyes.RightEye.Frown = expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R] * VDFaceTracking.EyeExpressionMult;
            eyes.RightEye.InnerBrowVertical = expressions[FBExpression.Inner_Brow_Raiser_R] + -expressions[FBExpression.Brow_Lowerer_R]; // Seems to fix eyebrows lowering
            eyes.RightEye.OuterBrowVertical = expressions[FBExpression.Outer_Brow_Raiser_R] + -expressions[FBExpression.Brow_Lowerer_R];
            eyes.RightEye.Squeeze = expressions[FBExpression.Brow_Lowerer_R];

            UpdateEye(eyes.RightEye, rightEyeData);

            if (eyes.LeftEye.IsTracking && eyes.RightEye.IsTracking)
            {
                eyes.CombinedEye.RawPosition = (eyes.LeftEye.RawPosition + eyes.RightEye.RawPosition) * 0.5f;
                eyes.CombinedEye.UpdateWithRotation(MathX.Slerp(eyes.LeftEye.RawRotation, eyes.RightEye.RawRotation, 0.5f));
            }
            else if (eyes.LeftEye.IsTracking)
            {
                eyes.CombinedEye.RawPosition = eyes.LeftEye.RawPosition;
                eyes.CombinedEye.UpdateWithRotation(eyes.LeftEye.RawRotation);
            }
            else if (eyes.RightEye.IsTracking)
            {
                eyes.CombinedEye.RawPosition = eyes.RightEye.RawPosition;
                eyes.CombinedEye.UpdateWithRotation(eyes.RightEye.RawRotation);
            }

            eyes.CombinedEye.IsTracking = eyes.LeftEye.IsTracking || eyes.RightEye.IsTracking;
            eyes.CombinedEye.PupilDiameter = 0.004f;

            eyes.LeftEye.Openness = MathX.Pow(1.0f - Math.Max(0, Math.Min(1, expressions[(int)Expressions.EyesClosedL] + expressions[(int)Expressions.EyesClosedL] * expressions[(int)Expressions.LidTightenerL])), VDFaceTracking.EyeOpenExponent);
            eyes.RightEye.Openness = MathX.Pow(1.0f - (float)Math.Max(0, Math.Min(1, expressions[(int)Expressions.EyesClosedR] + expressions[(int)Expressions.EyesClosedR] * expressions[(int)Expressions.LidTightenerR])), VDFaceTracking.EyeOpenExponent);

            eyes.ComputeCombinedEyeParameters();
            eyes.ConvergenceDistance = 0f;
            eyes.Timestamp += deltaTime;
            eyes.FinishUpdate();
        }

        void UpdateMouth(float deltaTime)
        {
            mouth.IsDeviceActive = Engine.Current.InputInterface.VR_Active;
            mouth.IsTracking = Engine.Current.InputInterface.VR_Active;

            // Pulled from Resonite:
            mouth.IsTracking = true;
            mouth.MouthLeftSmileFrown = expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L];
            mouth.MouthRightSmileFrown = expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R];
            mouth.MouthLeftDimple = expressions[FBExpression.Dimpler_L];
            mouth.MouthRightDimple = expressions[FBExpression.Dimpler_R];
            mouth.CheekLeftPuffSuck = expressions[FBExpression.Cheek_Puff_L] - expressions[FBExpression.Cheek_Suck_L];
            mouth.CheekRightPuffSuck = expressions[FBExpression.Cheek_Puff_R] - expressions[FBExpression.Cheek_Suck_R];
            mouth.CheekLeftRaise = expressions[FBExpression.Cheek_Raiser_L];
            mouth.CheekRightRaise = expressions[FBExpression.Cheek_Raiser_R];
            mouth.LipUpperLeftRaise = expressions[FBExpression.Upper_Lip_Raiser_L];
            mouth.LipUpperRightRaise = expressions[FBExpression.Upper_Lip_Raiser_R];
            mouth.LipLowerLeftRaise = expressions[FBExpression.Lower_Lip_Depressor_L];
            mouth.LipLowerRightRaise = expressions[FBExpression.Lower_Lip_Depressor_R];
            mouth.MouthPoutLeft = expressions[FBExpression.Lip_Pucker_L];
            mouth.MouthPoutRight = expressions[FBExpression.Lip_Pucker_R];
            mouth.LipUpperHorizontal = expressions[FBExpression.Mouth_Right] - expressions[FBExpression.Mouth_Left];
            mouth.LipLowerHorizontal = mouth.LipUpperHorizontal;
            mouth.LipTopLeftOverturn = expressions[FBExpression.Lip_Funneler_LT];
            mouth.LipTopRightOverturn = expressions[FBExpression.Lip_Funneler_RT];
            mouth.LipBottomLeftOverturn = expressions[FBExpression.Lip_Funneler_LB];
            mouth.LipBottomRightOverturn = expressions[FBExpression.Lip_Funneler_RB];
            mouth.LipTopLeftOverUnder = -expressions[FBExpression.Lip_Suck_LT];
            mouth.LipTopRightOverUnder = -expressions[FBExpression.Lip_Suck_RT];
            mouth.LipBottomLeftOverUnder = -expressions[FBExpression.Lip_Suck_LB];
            mouth.LipBottomRightOverUnder = -expressions[FBExpression.Lip_Suck_RB];
            mouth.LipLeftStretchTighten = expressions[FBExpression.Lip_Stretcher_L] - expressions[FBExpression.Lid_Tightener_L];
            mouth.LipRightStretchTighten = expressions[FBExpression.Lip_Stretcher_R] - expressions[FBExpression.Lid_Tightener_R];
            mouth.LipsLeftPress = expressions[FBExpression.Lip_Pressor_L];
            mouth.LipsRightPress = expressions[FBExpression.Lip_Pressor_R];
            mouth.Jaw = new float3(expressions[FBExpression.Jaw_Sideways_Right] - expressions[FBExpression.Jaw_Sideways_Left], -expressions[FBExpression.Lips_Toward], expressions[FBExpression.Jaw_Thrust]);
            mouth.JawOpen = MathX.Clamp01(expressions[FBExpression.Jaw_Drop] - expressions[FBExpression.Lips_Toward]);
            if (_extendedTongue)
                mouth.Tongue = new float3(expressions[FBExpression.TongueExtRight] - expressions[FBExpression.TongueExtLeft], expressions[FBExpression.TongueExtUp] - expressions[FBExpression.TongueExtDown], expressions[FBExpression.TongueExtOut]);
            else
                mouth.Tongue = new float3(0f, 0f, expressions[FBExpression.TongueOut] - expressions[FBExpression.TongueRetreat]);
            mouth.NoseWrinkleLeft = expressions[FBExpression.Nose_Wrinkler_L];
            mouth.NoseWrinkleRight = expressions[FBExpression.Nose_Wrinkler_R];
            mouth.ChinRaiseBottom = expressions[FBExpression.Chin_Raiser_B];
            mouth.ChinRaiseTop = expressions[FBExpression.Chin_Raiser_T];
        }
    }

    public enum FBEye
    {
        Left,
        Right
    }
}