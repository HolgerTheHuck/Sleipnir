import { mount } from 'svelte';
import App from './App.svelte';
import { closeWs } from './api';

mount(App, { target: document.getElementById('app')! });

window.addEventListener('beforeunload', () => {
  void closeWs();
});
