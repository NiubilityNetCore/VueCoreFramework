﻿import Vue from 'vue';
import { Component, Prop } from 'vue-property-decorator';

@Component
export default class ErrorComponent extends Vue {
    @Prop()
    code: string;

    errorMsg = '';

    mounted() {
        switch (this.code) {
            case '400':
                this.errorMsg = "Your request was invalid.";
                break;
            case '401':
                this.errorMsg = "You don't have permission to access this page.";
                break;
            case '403':
                this.errorMsg = "You don't have access to this page.";
                break;
            case '404':
                this.errorMsg = "Nothing was found here.";
                break;
            case '418':
                this.errorMsg = "I'm a teapot.";
                break;
        }
    }
}