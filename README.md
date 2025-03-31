# 📦 Infonuagique - TP3

## ✨ Overview

This project is a refactor of a mini social media platform composed of 4 ASP.NET Core apps:

- **MVC Web App** – Frontend with post creation and comments
- **API** – Allows programmatic access to posts/comments
- **Worker_Image** – Resizes uploaded images
- **Worker_Content** – Validates post content
- **🆕 Worker_DB** – New service responsible for all write operations to CosmosDB using Azure Event Hub

---

## 🏗️ Architecture Changes

### 🔁 Before (Old Flow)
- MVC/API directly **inserted data into the database**
- Workers processed images/validation and **also wrote to DB**
- Communications used **Service Bus** but DB writes were scattered

### 🔄 After (New Flow)
- MVC/API and Workers now **only emit events to Azure Event Hub**
- 🆕 **Worker_DB** is the **sole service responsible for all writes** to CosmosDB
- Ensures **centralized write logic**, **loose coupling**, and **scalability**

---

## 🧱 New Projects

### 🧩 `SharedEvents` (Class Library)
Contains serializable classes shared across apps for sending typed events.

**Implemented Events:**
- `PostCreatedEvent`
- `CommentCreatedEvent`
- `ImageResizedEvent`
- `ContentValidatedEvent`
- `SavePostToDbEvent`
- `SaveCommentToDbEvent`

---

### ⚙️ `Worker_DB`
A background service that listens to Azure Event Hub and writes to CosmosDB.

**Handled Events:**
- `SavePostToDbEvent`
- `SaveCommentToDbEvent`
- `ContentValidatedEvent`

**Technology Used:**
- `Azure.Messaging.EventHubs`
- `Cosmos DB SDK`
- `Newtonsoft.Json`

---

## 🔁 Changes in Existing Apps

### ✅ MVC & API Changes
- Injected `EventHubService`
- Replaced direct DB calls with `EventHubService.SendEventAsync(...)`
- Controllers now send `PostCreatedEvent`, `CommentCreatedEvent`, etc.

### ✅ Worker_Image & Worker_Content
- Now emit:
  - `ImageResizedEvent`
  - `ContentValidatedEvent`
- No longer write to the DB directly

### ✅ Repository Refactor
- All `.Add(...)` or `.Insert(...)` calls were **removed** from repositories
- Read operations remain unchanged

---

## 📁 Folder Summary

| Project          | Role                              | Notes                                    |
|------------------|-----------------------------------|------------------------------------------|
| `MVC`            | Frontend Web App                  | Emits PostCreated/CommentCreated events  |
| `API`            | Programmatic Interface            | Same as MVC, emits events only           |
| `Worker_Image`   | Image Resize Processing           | Emits ImageResizedEvent                  |
| `Worker_Content` | Content Validation Processing     | Emits ContentValidatedEvent              |
| `Worker_DB`      | Cosmos DB Writer via Event Hub    | Consumes all events and writes to DB     |
| `SharedEvents`   | Common Event Definitions          | Shared across all apps                   |

---

## 📦 Configuration

Update `appsettings.json` for each app:

```json
"EventHub": {
  "ConnectionString": "<your-event-hub-connection-string>",
  "Name": "<your-event-hub-name>"
}
