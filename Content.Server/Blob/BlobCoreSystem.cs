using System.Linq;
using System.Numerics;
using Content.Server.AlertLevel;
using Content.Server.Chat.Managers;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Fluids.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Mind;
using Content.Server.Roles;
using Content.Server.RoundEnd;
using Content.Server.Station.Systems;
using Content.Shared.Alert;
using Content.Shared.Backmen.Blob;
using Content.Shared.Blob;
using Content.Shared.Damage;
using Content.Shared.Destructible;
using Content.Shared.FixedPoint;
using Content.Shared.Mind;
using Content.Shared.Objectives;
using Content.Shared.Popups;
using Content.Shared.Weapons.Melee;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.Blob;



public sealed class BlobCoreSystem : EntitySystem
{
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly MindSystem _mindSystem = default!;
    [Dependency] private readonly RoleSystem _roleSystem = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly AudioSystem _audioSystem = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly BlobObserverSystem _blobObserver = default!;
    [Dependency] private readonly ExplosionSystem _explosionSystem = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly AlertLevelSystem _alertLevelSystem = default!;
    [Dependency] private readonly RoundEndSystem _roundEndSystem = default!;
    [Dependency] private readonly ActorSystem _actorSystem = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BlobCoreComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<BlobCoreComponent, DestructionEventArgs>(OnDestruction);
        SubscribeLocalEvent<BlobCoreComponent, DamageChangedEvent>(OnDamaged);
        SubscribeLocalEvent<BlobCoreComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<BlobCoreComponent, CreateBlobObserverEvent>(OnCreateBlobObserver);
    }

    private void OnCreateBlobObserver(EntityUid blobCoreUid, BlobCoreComponent core, CreateBlobObserverEvent args)
    {
        var observer = Spawn(core.ObserverBlobPrototype, Transform(blobCoreUid).Coordinates);

        core.Observer = observer;

        if (!TryComp<BlobObserverComponent>(observer, out var blobObserverComponent))
        {
            args.Cancel();
            return;
        }

        blobObserverComponent.Core = blobCoreUid;


        var isNewMind = false;
        if (!_mindSystem.TryGetMind(blobCoreUid, out var mindId, out var mind))
        {
            if (
                !_playerManager.TryGetSessionById(args.UserId, out var playerSession) ||
                playerSession.AttachedEntity == null ||
                !_mindSystem.TryGetMind(playerSession.AttachedEntity.Value, out mindId, out mind))
            {
                mindId = _mindSystem.CreateMind(args.UserId, "Blob Player");
                mind = Comp<MindComponent>(mindId);
                isNewMind = true;
            }
        }

        _mindSystem.SetUserId(mindId, args.UserId);
        _roleSystem.MindAddRole(mindId, new BlobRoleComponent{ PrototypeId = core.AntagBlobPrototypeId });
        SendBlobBriefing(mindId);

        _alerts.ShowAlert(observer, AlertType.BlobHealth, (short) Math.Clamp(Math.Round(core.CoreBlobTotalHealth.Float() / 10f), 0, 20));

        var blobRule = EntityQuery<BlobRuleComponent>().FirstOrDefault();
        blobRule?.Blobs.Add((mindId,mind));

        if (_prototypeManager.TryIndex<ObjectivePrototype>("BlobCaptureObjective", out var objective)
            && objective.CanBeAssigned(mindId, mind))
        {
            _mindSystem.TryAddObjective(mindId, mind, objective);
        }

        if (isNewMind)
        {
            _mindSystem.TransferTo(mindId, observer, true, mind: mind);
        }
        Timer.Spawn(1_000, () =>
        {
            _mindSystem.TransferTo(mindId, null, true, mind: mind);

            Timer.Spawn(1_000, () =>
            {
                _mindSystem.TransferTo(mindId, observer, true, mind: mind);
                if (_actorSystem.TryGetActorFromUserId(args.UserId, out var session, out _))
                {
                    _actorSystem.Attach(observer, session, true);
                }
                _blobObserver.UpdateUi(observer, blobObserverComponent);
            });
        });
    }

    private void OnPlayerAttached(EntityUid uid, BlobCoreComponent component, PlayerAttachedEvent args)
    {
        var xform = Transform(uid);
        if (!_mapManager.TryGetGrid(xform.GridUid, out var map))
            return;

        CreateBlobObserver(uid, args.Player.UserId, component);
    }

    public bool CreateBlobObserver(EntityUid blobCoreUid, NetUserId userId, BlobCoreComponent? core = null)
    {
        var xform = Transform(blobCoreUid);

        if (!Resolve(blobCoreUid, ref core))
            return false;

        var blobRule = EntityQuery<BlobRuleComponent>().FirstOrDefault();

        if (blobRule == null)
        {
            _gameTicker.StartGameRule("Blob", out _);
        }
        var ev = new CreateBlobObserverEvent(userId);
        RaiseLocalEvent(blobCoreUid, ev, true);

        return !ev.Cancelled;
    }

    private void SendBlobBriefing(EntityUid mind)
    {
        if (_mindSystem.TryGetSession(mind, out var session))
        {
            _chatManager.DispatchServerMessage(session, Loc.GetString("blob-role-greeting"));
        }
    }

    private void OnDamaged(EntityUid uid, BlobCoreComponent component, DamageChangedEvent args)
    {
        var maxHealth = component.CoreBlobTotalHealth;
        var currentHealth = maxHealth - args.Damageable.TotalDamage;

        if (component.Observer != null)
            _alerts.ShowAlert(component.Observer.Value, AlertType.BlobHealth, (short) Math.Clamp(Math.Round(currentHealth.Float() / 10f), 0, 20));
    }

    private void OnStartup(EntityUid uid, BlobCoreComponent component, ComponentStartup args)
    {
        ChangeBlobPoint(uid, 0, component);

        if (TryComp<BlobTileComponent>(uid, out var blobTileComponent))
        {
            blobTileComponent.Core = uid;
            blobTileComponent.Color = component.ChemСolors[component.CurrentChem];
            Dirty(blobTileComponent);
        }

        component.BlobTiles.Add(uid);

        ChangeChem(uid, component.DefaultChem, component);
    }

    public void ChangeChem(EntityUid uid, BlobChemType newChem, BlobCoreComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (newChem == component.CurrentChem)
            return;

        var oldChem = component.CurrentChem;
        component.CurrentChem = newChem;
        foreach (var blobTile in component.BlobTiles)
        {
            if (!TryComp<BlobTileComponent>(blobTile, out var blobTileComponent))
                continue;

            blobTileComponent.Color = component.ChemСolors[newChem];
            Dirty(blobTileComponent);

            if (TryComp<BlobFactoryComponent>(blobTile, out var blobFactoryComponent))
            {
                if (TryComp<BlobbernautComponent>(blobFactoryComponent.Blobbernaut, out var blobbernautComponent))
                {
                    blobbernautComponent.Color = component.ChemСolors[newChem];
                    Dirty(blobbernautComponent);

                    if (TryComp<MeleeWeaponComponent>(blobFactoryComponent.Blobbernaut, out var meleeWeaponComponent))
                    {
                        var blobbernautDamage = new DamageSpecifier();
                        foreach (var keyValuePair in component.ChemDamageDict[component.CurrentChem].DamageDict)
                        {
                            blobbernautDamage.DamageDict.Add(keyValuePair.Key, keyValuePair.Value * 0.8f);
                        }
                        meleeWeaponComponent.Damage = blobbernautDamage;
                    }

                    ChangeBlobEntChem(blobFactoryComponent.Blobbernaut.Value, oldChem, newChem);
                }

                foreach (var compBlobPod in blobFactoryComponent.BlobPods)
                {
                    if (TryComp<SmokeOnTriggerComponent>(compBlobPod, out var smokeOnTriggerComponent))
                    {
                        smokeOnTriggerComponent.SmokeColor = component.ChemСolors[newChem];
                    }
                }
            }

            ChangeBlobEntChem(blobTile, oldChem, newChem);
        }
    }

    private void OnDestruction(EntityUid uid, BlobCoreComponent component, DestructionEventArgs args)
    {
        if (component.Observer != null)
        {
            QueueDel(component.Observer.Value);
        }

        foreach (var blobTile in component.BlobTiles)
        {
            if (!TryComp<BlobTileComponent>(blobTile, out var blobTileComponent))
                continue;
            blobTileComponent.Core = null;

            blobTileComponent.Color = Color.White;
            Dirty(blobTileComponent);
        }

        var stationUid = _stationSystem.GetOwningStation(uid);
        var blobCoreQuery = EntityQueryEnumerator<BlobCoreComponent>();
        var isAllDie = 0;
        while (blobCoreQuery.MoveNext(out var ent, out var comp))
        {
            if (TerminatingOrDeleted(ent))
            {
                continue;
            }
            isAllDie++;
        }

        if (isAllDie <= 1)
        {
            var blobFactoryQuery = EntityQueryEnumerator<BlobRuleComponent>();
            while (blobFactoryQuery.MoveNext(out var blobRuleUid, out var blobRuleComp))
            {
                if (blobRuleComp.Stage == BlobStage.Critical ||
                    blobRuleComp.Stage == BlobStage.Begin)
                {
                    _alertLevelSystem.SetLevel(stationUid!.Value, "green", true, true, true, false);
                    _roundEndSystem.CancelRoundEndCountdown(null, false);
                }
            }
        }
        QueueDel(uid);
    }

    private void ChangeBlobEntChem(EntityUid uid, BlobChemType oldChem, BlobChemType newChem)
    {
        var explosionResistance = EnsureComp<ExplosionResistanceComponent>(uid);
        if (oldChem == BlobChemType.ExplosiveLattice)
        {
            _explosionSystem.SetExplosionResistance(uid, 0.3f, explosionResistance);
        }
        switch (newChem)
        {
            case BlobChemType.ExplosiveLattice:
                _damageable.SetDamageModifierSetId(uid, "ExplosiveLatticeBlob");
                _explosionSystem.SetExplosionResistance(uid, 0f, explosionResistance);
                break;
            case BlobChemType.ElectromagneticWeb:
                _damageable.SetDamageModifierSetId(uid, "ElectromagneticWebBlob");
                break;
            case BlobChemType.ReactiveSpines:
                _damageable.SetDamageModifierSetId(uid, "ReactiveSpinesBlob");
                break;
            default:
                _damageable.SetDamageModifierSetId(uid, "BaseBlob");
                break;
        }
    }

    public bool TransformBlobTile(EntityUid? oldTileUid, EntityUid coreTileUid, string newBlobTileProto,
        EntityCoordinates coordinates, BlobCoreComponent? blobCore = null, bool returnCost = true,
        FixedPoint2? transformCost = null)
    {
        if (!Resolve(coreTileUid, ref blobCore))
            return false;
        if (oldTileUid != null)
        {
            QueueDel(oldTileUid.Value);
            blobCore.BlobTiles.Remove(oldTileUid.Value);
        }
        var tileBlob = EntityManager.SpawnEntity(newBlobTileProto, coordinates);

        if (TryComp<BlobTileComponent>(tileBlob, out var blobTileComponent))
        {
            blobTileComponent.ReturnCost = returnCost;
            blobTileComponent.Core = coreTileUid;
            blobTileComponent.Color = blobCore.ChemСolors[blobCore.CurrentChem];
            Dirty(blobTileComponent);

            var explosionResistance = EnsureComp<ExplosionResistanceComponent>(tileBlob);

            if (blobCore.CurrentChem == BlobChemType.ExplosiveLattice)
            {
                _explosionSystem.SetExplosionResistance(tileBlob, 0f, explosionResistance);
            }
        }
        if (blobCore.Observer != null && transformCost != null)
        {
            _popup.PopupEntity(Loc.GetString("blob-spent-resource", ("point", transformCost)),
                tileBlob,
                blobCore.Observer.Value,
                PopupType.LargeCaution);
        }
        blobCore.BlobTiles.Add(tileBlob);
        return true;
    }

    public bool RemoveBlobTile(EntityUid tileUid, EntityUid coreTileUid, BlobCoreComponent? blobCore = null)
    {
        if (!Resolve(coreTileUid, ref blobCore))
            return false;

        QueueDel(tileUid);
        blobCore.BlobTiles.Remove(tileUid);

        return true;
    }

    public bool ChangeBlobPoint(EntityUid uid, FixedPoint2 amount, BlobCoreComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        component.Points += amount;

        if (component.Observer != null)
            _alerts.ShowAlert(component.Observer.Value, AlertType.BlobResource, (short) Math.Clamp(Math.Round(component.Points.Float() / 10f), 0, 16));

        return true;
    }

    public bool TryUseAbility(EntityUid uid, EntityUid coreUid, BlobCoreComponent component, FixedPoint2 abilityCost)
    {
        if (component.Points < abilityCost)
        {
            _popup.PopupEntity(Loc.GetString("blob-not-enough-resources"), uid, uid, PopupType.Large);
            return false;
        }

        ChangeBlobPoint(coreUid, -abilityCost, component);

        return true;
    }

    public bool CheckNearNode(EntityUid observer, EntityCoordinates coords, MapGridComponent grid, BlobCoreComponent core)
    {
        var radius = 3f;

        var innerTiles = grid.GetLocalTilesIntersecting(
            new Box2(coords.Position + new Vector2(-radius, -radius), coords.Position + new Vector2(radius, radius)), false).ToArray();

        foreach (var tileRef in innerTiles)
        {
            foreach (var ent in grid.GetAnchoredEntities(tileRef.GridIndices))
            {
                if (HasComp<BlobNodeComponent>(ent) || HasComp<BlobCoreComponent>(ent))
                    return true;
            }
        }

        _popup.PopupCoordinates(Loc.GetString("blob-target-nearby-not-node"), coords, observer, PopupType.Large);
        return false;
    }
}
