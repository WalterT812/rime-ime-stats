-- commit_logger.lua
-- 监听 Rime 上屏事件，把每次上屏的文字追加写入用户目录下的 commit_log.txt
-- 统计托盘程序（stats-app/ime_stats.py）会增量读取该文件统计中文字数。
-- 格式：时间戳 \t 上屏文本

local M = {}

local function log_path()
  return rime_api.get_user_data_dir() .. "/commit_log.txt"
end

function M.init(env)
  local context = env.engine.context
  env.notifier = context.commit_notifier:connect(function(ctx)
    local text = ctx:get_commit_text()
    if text and #text > 0 then
      local f = io.open(log_path(), "a")
      if f then
        f:write(os.date("%Y-%m-%d %H:%M:%S") .. "\t" .. text .. "\n")
        f:close()
      end
    end
  end)
end

function M.fini(env)
  if env.notifier then
    env.notifier:disconnect()
  end
end

-- 作为 processor 挂载，但不拦截任何按键（kNoop = 2）
function M.func(key_event, env)
  return 2
end

return M
