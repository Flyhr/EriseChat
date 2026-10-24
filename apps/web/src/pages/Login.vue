<script setup lang="ts">
import { ref } from 'vue'

// 登录/注册页（M1 实现：邮箱验证码、可选密码、服务器地址配置；接口见 api/auth.ts）。
const email = ref('')
const code = ref('')
const sending = ref(false)

function sendEmailCode(): void {
  // TODO(M1): 调用 authApi.sendEmailCode 并启动 60s 倒计时，错误按 ApiError.code 提示。
  sending.value = true
}

function login(): void {
  // TODO(M1): 校验后调用 authApi.login，成功写入 useAuthStore().setSession 并连接聊天。
  void login
}
</script>

<template>
  <main class="login-page">
    <section class="login-card">
      <h1 class="login-card__title">EiseChat</h1>
      <p class="login-card__subtitle">M0 骨架页面，登录流程将在 M1 接入</p>

      <form class="login-form" @submit.prevent="login">
        <label class="login-form__field">
          <span class="login-form__label">邮箱</span>
          <input v-model="email" type="email" autocomplete="email" placeholder="you@example.com" />
        </label>
        <label class="login-form__field">
          <span class="login-form__label">验证码</span>
          <input v-model="code" inputmode="numeric" maxlength="6" placeholder="6 位数字" />
        </label>
        <button class="login-form__submit" type="submit" :disabled="sending">
          {{ sending ? '发送中…' : '发送验证码（M1）' }}
        </button>
      </form>
    </section>
  </main>
</template>

<style scoped>
.login-page {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100%;
  padding: var(--space-5);
}

.login-card {
  width: 100%;
  max-width: 360px;
  padding: var(--space-6);
  background: var(--color-surface);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-md);
}

.login-card__title {
  margin: 0 0 var(--space-2);
  font-size: 24px;
  color: var(--color-primary);
}

.login-card__subtitle {
  margin: 0 0 var(--space-5);
  font-size: 13px;
  color: var(--color-text-secondary);
}

.login-form {
  display: grid;
  gap: var(--space-4);
}

.login-form__field {
  display: grid;
  gap: var(--space-2);
}

.login-form__label {
  font-size: 13px;
  color: var(--color-text-secondary);
}

.login-form input {
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
}

.login-form input:focus {
  border-color: var(--color-primary);
}

.login-form__submit {
  padding: var(--space-3);
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
}

.login-form__submit:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
