# Known Cache Issues — Presence / Push Notifications

This document lists the presence-cache problems that are still open or only partially mitigated. Push notifications are sent only to offline users, so any cache state that wrongly reports a user as online or offline directly affects notification delivery.

## 1. Stale online status ("ghost online") — partially mitigated

The key `{userId}_connection_established` is written on SignalR connect and deleted only in `OnDisconnectedAsync`. If the server crashes, restarts, or shuts down without firing disconnect events, the key survives in Redis until its absolute TTL expires. During that window the user appears online and receives no push notifications.

**Current mitigation:** the absolute TTL was reduced from 24 hours to 1 hour, so the worst-case window of lost notifications is now 1 hour instead of a full day.

**Full fix (pending):** replace the long absolute TTL with a short sliding TTL (2–5 minutes) refreshed by client activity or a heartbeat hub method. `StackExchangeRedisCache` refreshes sliding expiration on every `GetAsync`, so presence checks themselves keep the key alive while the user is genuinely connected. If the server dies, the key expires on its own within minutes.

## 2. Multiple devices per user — open

The presence key is per-user, not per-connection. When a user is connected from two devices and one of them disconnects, `OnDisconnectedAsync` deletes the shared key while the other connection is still alive. The user is then treated as offline and receives push notifications on a device where the app is open.

**Possible fixes:**
- Stop deleting the key on disconnect and rely on a short sliding TTL (see issue 1); the remaining active connection keeps the key alive.
- Track a connection count per user (`INCR` on connect, `DECR` on disconnect, online = count > 0). Requires direct `IConnectionMultiplexer` access because `IDistributedCache` has no atomic operations; combine with a TTL to survive missed disconnects.
- Use per-connection keys (`{userId}_{connectionId}_connection_established`) and treat the user as online when at least one exists.

## 3. Stale chat presence (`chat_entered`, `current_chat`) — open

Both `{userId}_chat_entered_{chatId}` and `{userId}_current_chat` are written with a 1-day absolute TTL and cleared only by the `chat-exited` hub method or `OnDisconnectedAsync`. A missed exit event leaves the user "inside the chat" for up to a day: incoming messages are marked as Seen without being seen, and no push notification is sent for them.

Additionally, `{userId}_current_chat` holds a single value per user. With two devices in different chats the value is overwritten by whichever device entered a chat last, and a disconnect of one device clears the state that belongs to the other.

**Possible fixes:** the same strategies as issues 1 and 2 — short sliding TTL maintained while the chat screen is open, and per-connection keys (`{userId}_{connectionId}_chat_entered_{chatId}`) checked as "at least one exists".
