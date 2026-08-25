"use strict";

const SENSITIVE_LOG_KEY_RE = /(?:password|passwd|secret|token|cookie|credential|private[_-]?key|api[_-]?key|client[_-]?secret|certificate[_-]?key|user(?:name)?|email)/i;
const USER_PATH_RE = /(?:[A-Za-z]:\\+Users\\+|\/+(?:home|Users)\/+)[^\s"'`,;]+/g;
const EMAIL_RE = /\b[^\s@,;]+@[^\s@,;]+\.[A-Za-z]{2,}\b/g;
const PEM_BLOCK_RE = /-----BEGIN [A-Z0-9 ]*(?:PRIVATE KEY|CERTIFICATE)-----[\s\S]*?-----END [A-Z0-9 ]*(?:PRIVATE KEY|CERTIFICATE)-----/g;

function redactLogText(value) {
  let text = String(value ?? "");
  text = text.replace(PEM_BLOCK_RE, "[REDACTED-PEM]");
  text = text.replace(/(\bAuthorization:\s*Bearer\s+)[^\r\n,;]+/gi, "$1[REDACTED]");
  text = text.replace(/(\bAuthorization:\s*)(?!\s*Bearer\b)[^\r\n,;]+/gi, "$1[REDACTED]");
  text = text.replace(/(\bauthorization\s*=\s*)[^\r\n,;& ]+/gi, "$1[REDACTED]");
  text = text.replace(/([?&](?:token|password|passwd|secret|api_key|username|user|email)=)[^&\r\n]+/gi, "$1[REDACTED]");
  text = text.split(/(\r?\n)/).map((part) => {
    if (part === "\n" || part === "\r\n") return part;
    return part.replace(
      new RegExp(`(\\b${SENSITIVE_LOG_KEY_RE.source}\\b\\s*[:=]\\s*)(?!REDACTED\\b)([^\\r\\n,;& ]+)`, "gi"),
      "$1[REDACTED]",
    );
  }).join("");
  text = text.replace(USER_PATH_RE, "[USER-PATH-REDACTED]");
  text = text.replace(EMAIL_RE, "[EMAIL-REDACTED]");
  return text;
}

function redactLogMessage(value, maxLength = 4096) {
  return redactLogText(String(value ?? "").slice(0, maxLength));
}

module.exports = {
  redactLogText,
  redactLogMessage,
};
