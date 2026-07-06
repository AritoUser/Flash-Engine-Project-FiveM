# Feature Proposal: Ephemeral Session Identity Tokens (Session Keys) for Player Routing & Security

## Goal
Introduce a unique, cryptographically secure, and ephemeral `SessionKey` (GUID) generated per player connection. This key will serve as the primary runtime identifier for script-to-script routing and client-to-server request authorization, mitigating NetID recycling issues and securing player state.

---

## The Problem
1. **NetID Recycling (State Poisoning):** FiveM recycles player NetIDs (e.g., `1`, `2`, `3`). If Player A (NetID `5`) disconnects, and Player B joins immediately after, Player B gets NetID `5`. Any asynchronous server operations (like DB queries, delayed tasks, or HTTP requests) still running for the old Player A will target NetID `5` and mistakenly apply their results or state to Player B.
2. **Lack of Secure Authorization:** In custom client-to-server RPCs or UI-initiated network events (NUI/CEF browser), using the player's database ID or license for authentication is insecure (guessable/spoofable) or exposes sensitive identifiers to the client environment.

---

## Proposed Specification

### 1. SDK Model Changes (`ServerPlayer`)
Add a `SessionKey` property to the `ServerPlayer` representation:

```csharp
public readonly struct ServerPlayer
{
    public int NetId { get; }
    public int AccountId { get; } // Persistent DB ID
    public string SessionKey { get; } // Ephemeral GUID
    
    // ... other properties ...
}
```

### 2. Lifecycle & Key Management
* **Generation:** When a player connects (`playerConnecting`), the server generates a cryptographically secure random GUID:
  ```csharp
  string sessionKey = Guid.NewGuid().ToString();
  ```
* **Registry Mapping:** The SDK maintains internal mappings:
  * `SessionKey -> ServerPlayer`
  * `NetId -> SessionKey`
* **Lookup API:** Provide quick lookups:
  * `Players.GetBySession(string sessionKey)`
* **Cleanup:** On disconnect (`playerDropped`), the `SessionKey` is invalidated, and the mappings are removed.

---

## Security & Documentation Requirements (To be added to Public Docs upon Implementation)

> [!IMPORTANT]
> When this feature is implemented, the following security rules and guidelines **must** be added to the public developer documentation to prevent misuse:

### 1. Strictly Enforce Confidentiality (No Broadcasting)
* **Rule:** A player's `SessionKey` must remain a strict secret shared only between the server and that specific client.
* **Warning:** Never store the `SessionKey` in public State Bags (e.g., `Entity(player).state.sessionKey = key`) or broadcast it to other clients via network events (e.g. `TriggerClientEvent("-1", ...)`). If a cheater obtains another player's `SessionKey`, they can spoof their identity and hijack their active session.

### 2. Secure Random Generation
* **Rule:** Always use cryptographically secure random number generators.
* **Warning:** Never use predictable pseudo-random number generators (like C# `new Random()`) or timestamps to generate session keys. Doing so allows attackers to predict future session keys and hijack joining player connections.

### 3. Handle Reconnect Reentrancy
* **Rule:** The connection manager must explicitly destroy and clean up any pre-existing session mapping to the same license/Account ID *before* establishing the new session and generating a new `SessionKey`.
* **Warning:** If a crashed connection's session is not cleaned up before the re-connection is established, state conflicts or memory leaks will occur in the session registry.

### 4. NUI Browser Security (XSS Protection)
* **Rule:** If the `SessionKey` is transmitted to the client's NUI (CEF browser) for web authorization, developer scripts must prevent Cross-Site Scripting (XSS).
* **Warning:** Avoid loading untrusted external JS files or images. An XSS exploit could allow malicious scripts to read the local storage/variables and steal the player's active `SessionKey`.
