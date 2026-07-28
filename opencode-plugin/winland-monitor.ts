/**
 * WinIsland Monitor Plugin for OpenCode
 *
 * Hooks session events and sends notifications to WinIsland's HTTP listener.
 * WinIsland displays task start/end notifications and live progress on the dynamic island.
 *
 * Install: place this file in ~/.config/opencode/plugins/ or .opencode/plugins/
 * Make sure WinIsland is running (it listens on http://127.0.0.1:47321).
 */

const WINLAND_PORT = 47321
const WINLAND_URL = `http://127.0.0.1:${WINLAND_PORT}`

interface WinLandEvent {
  type: "task_start" | "task_end" | "task_progress" | "task_error"
  sessionId?: string
  message?: string
  detail?: string
  toolName?: string
  progress?: number
}

async function sendToWinLand(payload: WinLandEvent) {
  try {
    await fetch(WINLAND_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    })
  } catch {
    // WinIsland not running — silently ignore
  }
}

export const WinLandMonitor = async ({ project, client, $, directory, worktree }) => {
  let sessionActive = false
  let toolCount = 0
  let messageCount = 0

  return {
    event: async ({ event }) => {
      switch (event.type) {
        case "session.created": {
          sessionActive = true
          toolCount = 0
          messageCount = 0
          await sendToWinLand({
            type: "task_start",
            sessionId: event.properties?.sessionId ?? event.properties?.id,
            message: project?.name ?? "OpenCode",
            detail: "AI 任务已开始",
          })
          break
        }

        case "session.idle": {
          if (!sessionActive) break
          sessionActive = false
          await sendToWinLand({
            type: "task_end",
            sessionId: event.properties?.sessionId ?? event.properties?.id,
            message: project?.name ?? "OpenCode",
            detail: "任务已完成",
          })
          break
        }

        case "session.error": {
          if (!sessionActive) break
          sessionActive = false
          await sendToWinLand({
            type: "task_error",
            sessionId: event.properties?.sessionId ?? event.properties?.id,
            message: project?.name ?? "OpenCode",
            detail: "任务出错",
          })
          break
        }

        case "tool.execute.before": {
          toolCount++
          const toolName = event.properties?.tool ?? "tool"
          await sendToWinLand({
            type: "task_progress",
            message: project?.name ?? "OpenCode",
            detail: `正在执行: ${toolName}`,
            toolName,
            progress: toolCount,
          })
          break
        }

        case "tool.execute.after": {
          await sendToWinLand({
            type: "task_progress",
            message: project?.name ?? "OpenCode",
            detail: `工具完成: ${event.properties?.tool ?? "tool"}`,
            toolName: event.properties?.tool ?? "tool",
            progress: toolCount,
          })
          break
        }

        case "message.updated": {
          messageCount++
          if (messageCount % 5 === 0) {
            await sendToWinLand({
              type: "task_progress",
              message: project?.name ?? "OpenCode",
              detail: `AI 思考中...`,
              progress: messageCount,
            })
          }
          break
        }

        case "todo.updated": {
          const todos = event.properties?.todos
          if (todos && Array.isArray(todos)) {
            const completed = todos.filter((t: any) => t.status === "completed").length
            const total = todos.length
            if (total > 0) {
              await sendToWinLand({
                type: "task_progress",
                message: project?.name ?? "OpenCode",
                detail: `进度: ${completed}/${total}`,
                progress: total > 0 ? completed / total : 0,
              })
            }
          }
          break
        }
      }
    },
  }
}
