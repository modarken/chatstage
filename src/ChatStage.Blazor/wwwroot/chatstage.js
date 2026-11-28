export function keyboard(input, receiver, submitOnEnter) {
    const keydown = event => {
        if (event.isComposing) return;
        if (event.key === 'Escape') receiver.invokeMethodAsync('Escape');
        if (submitOnEnter && event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
            event.preventDefault();
            receiver.invokeMethodAsync('SendAsync');
        }
    };
    input.addEventListener('keydown', keydown);
    return { dispose: () => input.removeEventListener('keydown', keydown) };
}
