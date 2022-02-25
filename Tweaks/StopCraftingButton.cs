﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.GeneratedSheets;
using SimpleTweaksPlugin.Helper;
using SimpleTweaksPlugin.TweakSystem;

namespace SimpleTweaksPlugin.Tweaks; 

// E8 ?? ?? ?? ?? 48 8B 4B 10 33 FF C6 83

public unsafe class StopCraftingButton : Tweak {
    
    private Stopwatch removeFrameworkUpdateEventStopwatch = new();

    private bool standingUp = false;
    
    public override string Name => "Improved Crafting Log";
    
    public override string Description => "Modifies the Synthesize button in the Crafting Log to swith job or stand up from the crafting position, allowing you to stop crafting without closing the crafting log.";
    
    private delegate byte EventFunction(EventFramework* eventFramework, uint a2, uint a3, uint a4);

    private delegate void* ClickSynthesisButton(void* a1, void* a2);
    
    private delegate*<CraftingState*, void*, void*> passthroughFunction;
    private delegate void* CancelCrafting(CraftingState* craftingState, void* a2);
    private HookWrapper<CancelCrafting> cancelCraftingHook;
    
    private HookWrapper<ClickSynthesisButton> clickSysnthesisButtonHook;

    [StructLayout(LayoutKind.Explicit)]
    private struct CraftingState {
        [FieldOffset(0x144)] public ushort Unknown;
    }
    
    private EventFunction eventFunction;
    private CraftingState* craftingState;
    
    
    [StructLayout(LayoutKind.Explicit, Size = 0xBFC)]
    public struct CraftingLogNumberArray {
        [FieldOffset(139 * 4)] public int RecipeCount;
        [FieldOffset(140 * 4)] public int Unknown140;
        [FieldOffset(141 * 4)] public RecipeList Recipes;

        [StructLayout(LayoutKind.Sequential, Size = 4 * 5 * 100)]
        public struct RecipeList {
            private fixed byte _data[4 * 5 * 100];
            public RecipeEntry* this[int i] {
                get {
                    if (i is < 0 or > 99) return null;
                    fixed (byte* p = _data) {
                        return (RecipeEntry*)(p + sizeof(RecipeEntry) * i);
                    }
                }
            }
        }
        
        [StructLayout(LayoutKind.Explicit, Size = 4 * 5)]
        public struct RecipeEntry {
            [FieldOffset(0 * 4)] public uint ResultItemId;
            [FieldOffset(1 * 4)] public uint ResultIconId;
            [FieldOffset(2 * 4)] public int Flags;
            [FieldOffset(3 * 4)] public int Level;
            [FieldOffset(4 * 4)] public ushort RecipeID;
        }

    }

    private HookWrapper<Common.AddonOnUpdate> craftingLogUpdateHook;
    
    public override void Enable() {
        eventFunction ??= Marshal.GetDelegateForFunctionPointer<EventFunction>(Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 33 C0 48 8B CB 66 89 83"));
        craftingState = (CraftingState*) Service.SigScanner.GetStaticAddressFromSig("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4B 10 33 FF");

        craftingLogUpdateHook ??= Common.HookAfterAddonUpdate("40 55 57 41 54 41 55 41 57 48 8D AC 24", CraftingLogUpdated);
        craftingLogUpdateHook?.Enable();

        clickSysnthesisButtonHook ??= Common.Hook<ClickSynthesisButton>("E9 ?? ?? ?? ?? 4C 8B 44 24 ?? 49 8B D2 48 8B CB 48 83 C4 30 5B E9 ?? ?? ?? ?? 4C 8B 44 24 ?? 49 8B D2 48 8B CB 48 83 C4 30 5B E9 ?? ?? ?? ?? 33 D2", ClickSynthesisButtonDetour);
        clickSysnthesisButtonHook?.Enable();

        passthroughFunction = (delegate*<CraftingState*, void*, void*>) Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 0F B6 C3 48 8B 5C 24 ?? 48 83 C4 20 5D");
        
        cancelCraftingHook ??= Common.Hook<CancelCrafting>("E8 ?? ?? ?? ?? 48 8B 4B 10 33 FF C6 83", CancelCraftingDetour);
        cancelCraftingHook?.Enable();

        base.Enable();
    }

    private void* CancelCraftingDetour(CraftingState* craftingstate, void* a2) {
        if (standingUp) return passthroughFunction(craftingstate, a2);
        return cancelCraftingHook.Original(craftingstate, a2);
    }

    private void ForceUpdate() {
        try {
            var addon = Common.GetUnitBase("RecipeNote");
            var atkArrayDataHolder = Framework.Instance()->GetUiModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
            if (addon != null) CraftingLogUpdated(addon, atkArrayDataHolder.NumberArrays, atkArrayDataHolder.StringArrays);
        } catch (Exception ex) {
            SimpleLog.Error(ex);
        }
    }
    
    private byte? GetGearsetForClassJob(uint cjId) {
        var gearsetModule = RaptureGearsetModule.Instance();
        for (var i = 0; i < 100; i++) {
            var gearset = gearsetModule->Gearset[i];
            if (gearset == null) continue;
            if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            if (gearset->ID != i) continue;
            if (gearset->ClassJob == cjId) return gearset->ID;
        }
        return null;
    }
    
    private void* ClickSynthesisButtonDetour(void* a1, void* a2) {
        try {

            uint requiredClass = 0;
            
            var readyState = GetCraftReadyState(ref requiredClass);
            switch (readyState) {
                case CraftReadyState.AlreadyCrafting: {
                    if (Service.ClientState.LocalPlayer != null) {
                        var localPlayer = (Character*) Service.ClientState.LocalPlayer.Address;
                        if (localPlayer->EventState == 5) {
                            eventFunction(EventFramework.Instance(), 6, 0, 0);
                            craftingState->Unknown = 0;
                            removeFrameworkUpdateEventStopwatch.Restart();
                            standingUp = true;
                            Service.Framework.Update += ForceUpdateFramework;
                            return null;
                        }
                    }

                    break;
                }
                case CraftReadyState.WrongClass: {
                    var gearset = GetGearsetForClassJob(requiredClass);
                    if (gearset != null) {
                        Plugin.XivCommon.Functions.Chat.SendMessage($"/gearset change {gearset.Value + 1}");
                        return null;
                    } 

                    Service.Chat.PrintError($"You have no saved gearset for {Service.Data.Excel.GetSheet<ClassJob>()?.GetRow(requiredClass)?.Name?.RawString ?? $"{requiredClass}"}.");
                    break;
                }
            }
        } catch {
            //
        }
        
        
        return clickSysnthesisButtonHook.Original(a1, a2);
    }

    private void ForceUpdateFramework(Dalamud.Game.Framework framework) {
        if (removeFrameworkUpdateEventStopwatch.ElapsedMilliseconds > 2000) framework.Update -= ForceUpdateFramework;
        ForceUpdate();
        if (standingUp == false || Service.ClientState.LocalPlayer == null) return;
        var localPlayer = (Character*) Service.ClientState.LocalPlayer.Address;
        if (localPlayer->EventState != 5) {
            standingUp = false;
        }
    }
    
    public enum CraftReadyState {
        NotReady,
        Ready,
        WrongClass,
        AlreadyCrafting,
    }

    private CraftReadyState GetCraftReadyState() {
        uint requiredClass = 0;
        return GetCraftReadyState(ref requiredClass);
    }
    
    private CraftReadyState GetCraftReadyState(ref uint requiredClass) {
        if (Service.ClientState.LocalPlayer == null) return CraftReadyState.NotReady;
        var agentRecipeNote = AgentRecipeNote.Instance();
        var atkArrayDataHolder = Framework.Instance()->GetUiModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
        var craftingLogRawNumberArray = atkArrayDataHolder.NumberArrays[26];
        var craftingLogNumberArray = (CraftingLogNumberArray*) craftingLogRawNumberArray->IntArray;
        var selectedRecipeData = craftingLogNumberArray->Recipes[agentRecipeNote->SelectedRecipeIndex];
        if (selectedRecipeData == null) return CraftReadyState.NotReady;
        var selectedRecipe = Service.Data.Excel.GetSheet<Recipe>()?.GetRow(selectedRecipeData->RecipeID);
        if (selectedRecipe == null) return CraftReadyState.NotReady;
        var recipeJobId = selectedRecipe.CraftType.Row + 8;
        requiredClass = recipeJobId;
        var requiredJob = Service.Data.Excel.GetSheet<ClassJob>()?.GetRow(recipeJobId);
        if (requiredJob == null) return CraftReadyState.NotReady;
        if (Service.ClientState.LocalPlayer.ClassJob.Id == recipeJobId) return CraftReadyState.Ready;
        var localPlayer = (Character*) Service.ClientState.LocalPlayer.Address;
        return localPlayer->EventState == 5 ? CraftReadyState.AlreadyCrafting : CraftReadyState.WrongClass;
    }
    

    private void CraftingLogUpdated(AtkUnitBase* atkUnitBase, NumberArrayData** numberArrayData, StringArrayData** stringArrayData) {
        var ready = GetCraftReadyState();
        if (ready == CraftReadyState.NotReady) return;
        var craftButton = (AtkComponentNode*) atkUnitBase->GetNodeById(103);
        if (craftButton->AtkResNode.Type != (NodeType)1005) return;
        var craftButtonComp = (AtkComponentButton*) craftButton->Component;
        if (craftButtonComp == null || craftButtonComp->ButtonTextNode == null) return;
        
        var buttonText = ready switch {
            CraftReadyState.Ready => Service.Data.Excel.GetSheet<Addon>()?.GetRow(1404)?.Text?.ToDalamudString(),
            CraftReadyState.WrongClass => "Switch Job",
            CraftReadyState.AlreadyCrafting => Service.Data.Excel.GetSheet<Addon>()?.GetRow(643)?.Text?.ToDalamudString(),
            _ => null
        };

        if (buttonText != null) {
            craftButtonComp->ButtonTextNode->SetText(buttonText.Encode());
        }
    }

    private void CloseCraftingLog() {
        var rl = Common.GetUnitBase("RecipeNote");
        if (rl != null) UiHelper.Close(rl, true);
    }
    
    
    public override void Disable() {
        craftingLogUpdateHook?.Disable();
        clickSysnthesisButtonHook?.Disable();
        CloseCraftingLog();
        cancelCraftingHook?.Disable();
        base.Disable();
    }

    public override void Dispose() {
        craftingLogUpdateHook?.Dispose();
        clickSysnthesisButtonHook?.Dispose();
        cancelCraftingHook?.Dispose();
        base.Dispose();
    }
}

