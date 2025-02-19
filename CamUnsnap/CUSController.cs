using EFT;
using EFT.UI;
using System;
using System.Linq;
using UnityEngine;
using Comfort.Common;
using System.Reflection;
using EFT.Communications;
using MonoMod.RuntimeDetour;
using System.Threading.Tasks;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace CamUnsnap
{
    // I love documentation
    /// <summary>
    /// Represents a stream of positions and angles captured from a parent GameObject
    /// </summary>
    public struct TransformRecording
    {
        /// <summary>
        /// Creates a new transform recording stream
        /// </summary>
        /// <param name="Target">The GameObject to capture transform data from</param>
        public TransformRecording(GameObject Target)
        {
            this.Target = Target;
            Positions = new List<Vector3>();
            Angles = new List<Vector3>();
        }

        /// <summary>
        /// Captures and saves the position and angles of the target GameObject on the current frame
        /// </summary>
        public void Capture()
        {
            Positions.Add(Target.transform.position);
            Angles.Add(Target.transform.localEulerAngles);
        }

        /// <summary>
        /// Clears all recorded position and angle streams
        /// </summary>
        public void Clear()
        {
            Positions = new List<Vector3>();
            Angles = new List<Vector3>();
        }

        /// <summary>
        /// Checks if any values are present in the current streams
        /// </summary>
        /// <returns>true if there are any non-null values, false otherwise</returns>
        public bool Any() => Positions.Any();

        public Vector3[] this[int index]
        {
            get => new Vector3[] { Positions[index], Angles[index] };
        }

        public int Length
        { get => Positions.Count - 1; }

        public List<Vector3> Positions
        { get; private set; }

        public List<Vector3> Angles
        { get; private set; }

        public readonly GameObject Target;
    }

    public class CUSController : MonoBehaviour
    {
        bool mCamUnsnapped = false;
        bool Recording = false;
        bool playingPath = false;
        int currentRecordingIndex = 0;
        GameObject gameCamera;
        Vector2 smoothedMouseDelta;
        Vector2 currentMouseDelta;
        Vector3 MemoryPos;
        List<Detour> Detours = new List<Detour>();
        List<Vector3> MemoryPosList = new List<Vector3>();
        TransformRecording PathRecording;
        int currentListIndex = 0;
        float cacheFOV = 0;

        bool CamViewInControl { get; set; } = false;

        Player player
        { get => gameWorld.MainPlayer; }

        GameWorld gameWorld
        { get => Singleton<GameWorld>.Instance; }

        float MovementSpeed
        { get => Plugin.MovementSpeed.Value; }

        float CameraSensitivity
        { get => Plugin.CameraSensitivity.Value; }

        float CameraSmoothing
        { get => Plugin.CameraSmoothing.Value; }

        GameObject commonUI
        { get => MonoBehaviourSingleton<CommonUI>.Instance.gameObject; }

        GameObject preloaderUI
        { get => MonoBehaviourSingleton<PreloaderUI>.Instance.gameObject; }

        GameObject gameScene
        { get => MonoBehaviourSingleton<GameUI>.Instance.gameObject; }

        bool GamespeedChanged
        {
            get => Time.timeScale != 1f;
            set
            {
                Time.timeScale = value ? Plugin.Gamespeed.Value : 1f;
            }
        }

        bool UIEnabled
        {
            get => commonUI.activeSelf && preloaderUI.activeSelf && gameScene.activeSelf;
            set
            {
                commonUI.SetActive(value);
                preloaderUI.SetActive(value);
                gameScene.SetActive(value);
            }
        }

        bool playerAirborne
        {
            get => !player.GetCharacterControllerCommon().isGrounded;
        }

        bool CamUnsnapped
        {
            get => mCamUnsnapped;
            set
            {
                if (!value)
                {
                    if (!Plugin.OverrideGameRestriction.Value)
                    {
                        if (Ready())
                        {
                            player.PointOfView = EPointOfView.FirstPerson;
                            if (Plugin.ImmuneInCamera.Value) player.ActiveHealthController.SetDamageCoeff(1);
                        }
                        if (Detours.Any()) Detours.ForEach((Detour det) => det.Dispose()); Detours.Clear();
                        if (!UIEnabled)
                        {
                            try
                            {
                                commonUI.SetActive(true);
                                preloaderUI.SetActive(true);
                                gameScene.SetActive(true);
                            }
                            catch (Exception e) { Plugin.logger.LogError($"bruh\n{e}"); }
                            UIEnabled = true;
                        }
                        Camera.current.fieldOfView = cacheFOV;
                    }
                }
                else
                {
                    if (player != null)
                    {
                        player.PointOfView = EPointOfView.FreeCamera;
                        player.PointOfView = EPointOfView.ThirdPerson;
                    }

                    cacheFOV = Camera.current.fieldOfView;
                    if (Plugin.OverrideGameRestriction.Value) SendNotificaiton("Session Override is enabled, player and positioning options are ignored, and controlling the camera outside of a raid may cause issues.\nYou've been warned...");
                }
                mCamUnsnapped = value;
            }
        }

        void Update()
        {
            if (IsKeyPressed(Plugin.ToggleCameraSnap.Value))
                CamUnsnapped = !CamUnsnapped;

            if (IsKeyPressed(Plugin.CameraMouse.Value))
                CamViewInControl = !CamViewInControl;

            if (IsKeyPressed(Plugin.ChangeGamespeed.Value) && CamUnsnapped)
                GamespeedChanged = !GamespeedChanged;

            if (CamUnsnapped)
            {
                try
                {
                    gameCamera = Camera.current.gameObject;

                    if (!Plugin.OverrideGameRestriction.Value && Ready())
                    {
                        if (PathRecording.Target == null)
                        {
                            PathRecording = new TransformRecording(gameCamera);
                        }

                        if (IsKeyPressed(Plugin.GoToPos.Value))
                        {
                            if (MemoryPos == null)
                                SendNotificaiton("No memory pos to move camera to.");
                            else
                                gameCamera.transform.position = MemoryPos;
                        }

                        if (IsKeyPressed(Plugin.HideUI.Value))
                            UIEnabled = !UIEnabled;

                        if (IsKeyPressed(Plugin.PlayRecord.Value))
                            playingPath = true;

                        if (Recording)
                            PathRecording.Capture();

                        if (playingPath)
                        {
                            Vector3[] transformFrame = PathRecording[currentRecordingIndex];
                            gameCamera.transform.position = transformFrame[0];
                            gameCamera.transform.localEulerAngles = transformFrame[1];

                            currentRecordingIndex++;

                            if (currentRecordingIndex > PathRecording.Length) // fuckers took my .Length cant have shit with VS
                            {
                                currentRecordingIndex = 0;
                                playingPath = false;
                                return;
                            }

                            return;
                        }

                        if (IsKeyPressed(Plugin.MovePlayerToCam.Value))
                            MovePlayer();

                        if (IsKeyPressed(Plugin.BeginRecord.Value))
                        {
                            Recording = true;
                            PathRecording.Clear();
                            SendNotificaiton("Recording Started", false);
                        }

                        if (IsKeyPressed(Plugin.ResumeRecord.Value))
                        {
                            if (PathRecording.Any())
                            {
                                Recording = true;
                                SendNotificaiton("Recording Resumed", false);
                            }
                            else
                            {
                                SendNotificaiton($"Cannot resume recording\nNo previous recording exists, press '{Plugin.BeginRecord.Value}' to start a new one");
                            }
                        }

                        if (IsKeyPressed(Plugin.StopRecord.Value))
                        {
                            Recording = false;
                            SendNotificaiton("Recording Stopped", false);
                        }

                        player.ActiveHealthController.SetDamageCoeff(Plugin.ImmuneInCamera.Value ? 0f : player.ActiveHealthController.DamageCoeff != 1f && !playerAirborne ? 1f : 0f);

                        if (IsKeyPressed(Plugin.RememberPos.Value))
                            MemoryPos = gameCamera.transform.position;

                        if (IsKeyPressed(Plugin.LockPlayerMovement.Value))
                        {
                            if (!Detours.Any())
                                Detours = new List<Detour>()
                                {
                                    new Detour(typeof(Player).GetMethod(nameof(Player.Move)).CreateDelegate(player), (Action)BlankOverride),
                                    new Detour(typeof(Player).GetMethod(nameof(Player.Rotate)).CreateDelegate(player), (Action)BlankOverride),
                                    new Detour(typeof(Player).GetMethod(nameof(Player.SlowLean)).CreateDelegate(player), (Action)BlankOverride),
                                    new Detour(typeof(Player).GetMethod(nameof(Player.ChangePose)).CreateDelegate(player), (Action)BlankOverride),
                                    new Detour(typeof(Player).GetMethod(nameof(Player.Jump)).CreateDelegate(player), (Action)BlankOverride),
                                    new Detour(typeof(Player).GetMethod(nameof(Player.ToggleProne)).CreateDelegate(player), (Action)BlankOverride)
                                };
                            else
                            {
                                Detours.ForEach((Detour det) => det.Dispose());
                                Detours.Clear();
                            };
                        }

                        if (IsKeyPressed(Plugin.AddToMemPosList.Value))
                            MemoryPosList.Add(gameCamera.transform.position);

                        if (IsKeyPressed(Plugin.AdvanceList.Value))
                        {
                            if (MemoryPosList[currentListIndex + 1] != null)
                            {
                                currentListIndex++;
                                gameCamera.transform.position = MemoryPosList[currentListIndex];
                            }
                            else if (MemoryPosList.First() != null)
                            {
                                currentListIndex = 0;
                                gameCamera.transform.position = MemoryPosList.First();
                            }
                            else
                            {
                                currentListIndex = 0;
                                SendNotificaiton("No valid Vector3 in Memory Position List to move to.");
                            }
                        }

                        if (IsKeyPressed(Plugin.ClearList.Value))
                            MemoryPosList.Clear();

                    }
                    else if (!Ready() && MemoryPosList.Any())
                    {
                        MemoryPosList.Clear();
                        CamUnsnapped = false;
                        return;
                    }

                    float delta = !GamespeedChanged ? Time.deltaTime : Time.fixedDeltaTime;
                    float fastMove = IsKeyDown(Plugin.FastMove.Value) ? Plugin.FastMoveMult.Value : 1f;
                    Camera.current.fieldOfView = Plugin.CameraFOV.Value;

                    if (IsKeyDown(Plugin.CamLeft.Value))
                        gameCamera.transform.position += (-gameCamera.transform.right * MovementSpeed * fastMove * delta);

                    if (IsKeyDown(Plugin.CamRight.Value))
                        gameCamera.transform.position += (gameCamera.transform.right * MovementSpeed * fastMove * delta);

                    if (IsKeyDown(Plugin.CamForward.Value))
                        gameCamera.transform.position += (gameCamera.transform.forward * MovementSpeed * fastMove * delta);

                    if (IsKeyDown(Plugin.CamBack.Value))
                        gameCamera.transform.position += (-gameCamera.transform.forward * MovementSpeed * fastMove * delta);

                    if (IsKeyDown(Plugin.CamUp.Value))
                        gameCamera.transform.position += (gameCamera.transform.up * MovementSpeed * fastMove * delta);

                    if (IsKeyDown(Plugin.CamDown.Value))
                        gameCamera.transform.position += (-gameCamera.transform.up * MovementSpeed * fastMove * delta);

                    if (CamViewInControl)
                    {

                        currentMouseDelta.x = Input.GetAxis("Mouse X") * CameraSensitivity;
                        currentMouseDelta.y = Input.GetAxis("Mouse Y") * CameraSensitivity;

                        smoothedMouseDelta = Vector2.Lerp(smoothedMouseDelta, currentMouseDelta, CameraSmoothing);

                        float newRotationX = gameCamera.transform.localEulerAngles.y + smoothedMouseDelta.x;
                        float newRotationY = gameCamera.transform.localEulerAngles.x - smoothedMouseDelta.y;

                        gameCamera.transform.localEulerAngles = new Vector3(newRotationY, newRotationX, 0f);

                    }

                    if (IsKeyDown(Plugin.RotateLeft.Value))
                        gameCamera.transform.localEulerAngles += new Vector3(0, 0, Plugin.RotateUsesSens.Value ? 1f * CameraSensitivity : 1f);

                    if (IsKeyDown(Plugin.RotateRight.Value))
                        gameCamera.transform.localEulerAngles += new Vector3(0, 0, Plugin.RotateUsesSens.Value ? -1f * CameraSensitivity : -1f);

                }
                catch (Exception e)
                {
                    SendNotificaiton($"Camera machine broke =>\n{e.Message}");
                    Plugin.logger.LogError(e);
                    CamUnsnapped = false;
                }
            }
        }

        async void MovePlayer()
        {
            player.Transform.position = gameCamera.transform.position;
            player.ActiveHealthController.SetDamageCoeff(1);
            while (playerAirborne)
            {
                await Task.Yield();
            }
            player.ActiveHealthController.SetDamageCoeff(1);
        }

        void SendNotificaiton(string message, bool warn = true) => NotificationManagerClass.DisplayMessageNotification(message, ENotificationDurationType.Long, warn ? ENotificationIconType.Alert : ENotificationIconType.Default);

        public static void BlankOverride() { } // override so player doesn't move

        bool Ready() => gameWorld != null && gameWorld.AllAlivePlayersList != null && gameWorld.AllAlivePlayersList.Count > 0;

        // Custom KeyDown check that handles modifiers, but also lets you hit more than one key at a time
        bool IsKeyDown(KeyboardShortcut key)
        {
            if (!Input.GetKey(key.MainKey))
            {
                return false;
            }

            foreach (var modifier in key.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }

            return true;
        }

        // Custom KeyPressed check that handles modifiers, but also lets you hit more than one key at a time
        bool IsKeyPressed(KeyboardShortcut key)
        {
            if (!Input.GetKeyDown(key.MainKey))
            {
                return false;
            }

            foreach (var modifier in key.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
